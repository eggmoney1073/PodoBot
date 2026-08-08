using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PodoBot;

public sealed class ChzzkSessionClient : IAsyncDisposable
{
    private readonly ChzzkApiClient _api;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _connectionCts;
    private string _accessToken = "";
    private TimeSpan _pingInterval = TimeSpan.FromSeconds(25);
    private bool _isConnected;

    public event Func<ChatEvent, Task>? ChatReceived;
    public event Func<DonationEvent, Task>? DonationReceived;
    public event Action<string>? Log;
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _isConnected && _socket?.State == WebSocketState.Open;

    public ChzzkSessionClient(ChzzkApiClient api) => _api = api;

    public async Task ConnectAsync(string accessToken)
    {
        await DisconnectAsync();
        _accessToken = accessToken;

        var sessionUrl = await _api.CreateSessionUrlAsync(accessToken);
        var socketUri = BuildSocketUri(sessionUrl);
        var socket = new ClientWebSocket();
        var cts = new CancellationTokenSource();
        _socket = socket;
        _connectionCts = cts;

        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(12));
            await socket.ConnectAsync(socketUri, connectTimeout.Token);

            var openPacket = await ReceiveTextAsync(socket, connectTimeout.Token);
            if (string.IsNullOrWhiteSpace(openPacket) || openPacket[0] != '0')
                throw new InvalidOperationException("치지직 Engine.IO handshake를 받지 못했습니다.");

            ReadEngineOpenPacket(openPacket);

            while (!connectTimeout.IsCancellationRequested)
            {
                var packet = await ReceiveTextAsync(socket, connectTimeout.Token);
                if (packet is null)
                    throw new InvalidOperationException("치지직 서버가 세션 연결을 종료했습니다.");
                if (packet == "40") break;
                if (packet.StartsWith("41", StringComparison.Ordinal))
                    throw new InvalidOperationException("치지직 서버가 Socket.IO 연결을 거부했습니다.");

                await HandlePacketAsync(packet, socket, connectTimeout.Token);
            }

            SetConnected(true);
            Log?.Invoke("치지직 실시간 연결 완료");
            _ = ReceiveLoopAsync(socket, cts.Token);
            _ = HeartbeatLoopAsync(socket, cts.Token);
        }
        catch
        {
            await DisconnectAsync();
            throw;
        }
    }

    private static Uri BuildSocketUri(string sessionUrl)
    {
        var source = new Uri(sessionUrl);
        var builder = new UriBuilder(source)
        {
            Scheme = source.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "/socket.io/"
        };

        var query = source.Query.TrimStart('?');
        if (!string.IsNullOrWhiteSpace(query)) query += "&";
        query += "EIO=3&transport=websocket";
        builder.Query = query;
        return builder.Uri;
    }

    private void ReadEngineOpenPacket(string packet)
    {
        try
        {
            using var doc = JsonDocument.Parse(packet[1..]);
            if (doc.RootElement.TryGetProperty("pingInterval", out var interval)
                && interval.TryGetInt32(out var milliseconds)
                && milliseconds > 0)
            {
                _pingInterval = TimeSpan.FromMilliseconds(milliseconds);
            }
        }
        catch
        {
            _pingInterval = TimeSpan.FromSeconds(25);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var disconnectReason = "transport close";

        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var packet = await ReceiveTextAsync(socket, cancellationToken);
                if (packet is null) break;
                if (packet.StartsWith("41", StringComparison.Ordinal))
                {
                    disconnectReason = "io server disconnect";
                    break;
                }

                await HandlePacketAsync(packet, socket, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            disconnectReason = "io client disconnect";
        }
        catch (Exception ex)
        {
            disconnectReason = ex.Message;
            Log?.Invoke($"실시간 연결 오류: {ex.Message}");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetConnected(false);
                Log?.Invoke($"연결 끊김: {disconnectReason}");
            }
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await Task.Delay(_pingInterval, cancellationToken);
                await SendTextAsync(socket, "2", cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log?.Invoke($"세션 heartbeat 오류: {ex.Message}"); }
    }

    private async Task HandlePacketAsync(string packet, ClientWebSocket socket, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(packet)) return;

        switch (packet[0])
        {
            case '0':
                ReadEngineOpenPacket(packet);
                return;
            case '2':
                await SendTextAsync(socket, "3" + packet[1..], cancellationToken);
                return;
            case '3':
                return;
            case '4':
                await HandleSocketIoPacketAsync(packet[1..]);
                return;
        }
    }

    private async Task HandleSocketIoPacketAsync(string packet)
    {
        if (string.IsNullOrEmpty(packet) || packet[0] != '2') return;

        using var doc = JsonDocument.Parse(packet[1..]);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 2) return;

        var eventName = doc.RootElement[0].GetString() ?? "";
        var data = NormalizeEventData(doc.RootElement[1]);
        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;

        try
        {
            if (eventName == "SYSTEM")
            {
                await HandleSystemAsync(data);
                return;
            }

            if (eventName == "CHAT")
            {
                var chat = ParseChat(data);
                if (ChatReceived is not null) await ChatReceived.Invoke(chat);
                return;
            }

            if (eventName == "DONATION")
            {
                var donation = ParseDonation(data);
                if (DonationReceived is not null) await DonationReceived.Invoke(donation);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"{eventName} 처리 오류: {ex.Message}");
        }
    }

    private static JsonElement NormalizeEventData(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.String) return data.Clone();
        var json = data.GetString();
        if (string.IsNullOrWhiteSpace(json)) return default;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private async Task HandleSystemAsync(JsonElement data)
    {
        if (!data.TryGetProperty("type", out var typeElement)) return;
        var type = typeElement.GetString() ?? "";

        if (type == "connected")
        {
            var sessionKey = data.GetProperty("data").GetProperty("sessionKey").GetString() ?? "";
            await _api.SubscribeChatAsync(_accessToken, sessionKey);
            Log?.Invoke("채팅 읽기 준비 완료");

            try
            {
                await _api.SubscribeDonationAsync(_accessToken, sessionKey);
                Log?.Invoke("후원 읽기 준비 완료");
            }
            catch (Exception ex)
            {
                // Donation scope is optional. Chat stays connected even if donation subscription fails.
                Log?.Invoke($"후원 기능 비활성: {ex.Message}");
            }
            return;
        }

        if (type == "subscribed")
        {
            var eventType = "";
            if (data.TryGetProperty("data", out var info) && info.ValueKind == JsonValueKind.Object)
                eventType = Get(info, "eventType");

            if (eventType.Equals("DONATION", StringComparison.OrdinalIgnoreCase))
                Log?.Invoke("후원 구독 완료");
            else if (eventType.Equals("CHAT", StringComparison.OrdinalIgnoreCase))
                Log?.Invoke("채팅 구독 완료");
            else
                Log?.Invoke("이벤트 구독 완료");
            return;
        }

        if (type == "revoked")
            Log?.Invoke("치지직 이벤트 권한이 해제되었습니다.");
    }

    private static ChatEvent ParseChat(JsonElement data)
    {
        var profile = data.TryGetProperty("profile", out var value) ? value : default;
        return new ChatEvent
        {
            ChannelId = Get(data, "channelId"),
            SenderChannelId = Get(data, "senderChannelId"),
            Content = Get(data, "content"),
            Nickname = profile.ValueKind == JsonValueKind.Object ? Get(profile, "nickname") : "",
            UserRoleCode = profile.ValueKind == JsonValueKind.Object ? Get(profile, "userRoleCode") : "",
            MessageTime = data.TryGetProperty("messageTime", out var time) && time.TryGetInt64(out var timestamp) ? timestamp : 0
        };
    }

    private static DonationEvent ParseDonation(JsonElement data)
    {
        var amountText = Get(data, "payAmount").Replace(",", "");
        long.TryParse(amountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount);

        return new DonationEvent
        {
            DonationType = Get(data, "donationType"),
            ChannelId = Get(data, "channelId"),
            DonatorChannelId = Get(data, "donatorChannelId"),
            DonatorNickname = Get(data, "donatorNickname"),
            PayAmount = amount,
            DonationText = Get(data, "donationText")
        };
    }

    private static string Get(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString()
        };
    }

    public async Task DisconnectAsync()
    {
        var socket = _socket;
        var cts = _connectionCts;
        _socket = null;
        _connectionCts = null;
        cts?.Cancel();

        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "PodoBot disconnect", CancellationToken.None);
            }
            catch { }
            socket.Dispose();
        }

        cts?.Dispose();
        SetConnected(false);
    }

    private async Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
            {
                if (result.EndOfMessage) return "";
                continue;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private void SetConnected(bool connected)
    {
        if (_isConnected == connected) return;
        _isConnected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _sendGate.Dispose();
    }
}
