using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PodoBot;

public sealed class ChzzkSessionClient : IAsyncDisposable
{
    private readonly ChzzkApiClient _api;
    private SocketIO? _socket;
    private string _accessToken = "";

    public event Func<ChatEvent, Task>? ChatReceived;
    public event Action<string>? Log;
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _socket?.Connected == true;

    public ChzzkSessionClient(ChzzkApiClient api)
    {
        _api = api;
    }

    public async Task ConnectAsync(string accessToken)
    {
        await DisconnectAsync();

        _accessToken = accessToken;
        var sessionUrl = await _api.CreateSessionUrlAsync(accessToken);

        var options = new SocketIOOptions
        {
            EIO = EngineIO.V3,
            Reconnection = true,
            ReconnectionAttempts = 8,
            ReconnectionDelay = 1500,
            ConnectionTimeout = TimeSpan.FromSeconds(12),
            Transport = TransportProtocol.WebSocket,
            AutoUpgrade = false
        };

        _socket = new SocketIO(new Uri(sessionUrl), options);

        _socket.OnConnected += (_, _) =>
        {
            Log?.Invoke("치지직 실시간 연결 완료");
            ConnectionChanged?.Invoke(true);
        };

        _socket.OnDisconnected += (_, reason) =>
        {
            Log?.Invoke($"연결 끊김: {reason}");
            ConnectionChanged?.Invoke(false);
        };

        _socket.OnError += (_, error) =>
        {
            Log?.Invoke($"실시간 연결 오류: {error}");
        };

        _socket.On("SYSTEM", async ctx =>
        {
            try
            {
                var data = ctx.GetValue<JsonElement>(0);
                await HandleSystemAsync(data);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"세션 처리 오류: {ex.Message}");
            }
        });

        _socket.On("CHAT", async ctx =>
        {
            try
            {
                var data = ctx.GetValue<JsonElement>(0);
                var chat = ParseChat(data);

                if (ChatReceived is not null)
                    await ChatReceived.Invoke(chat);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"채팅 처리 오류: {ex.Message}");
            }
        });

        await _socket.ConnectAsync();
    }

    private async Task HandleSystemAsync(JsonElement data)
    {
        if (!data.TryGetProperty("type", out var typeElement))
            return;

        var type = typeElement.GetString() ?? "";

        if (type == "connected")
        {
            var sessionKey = data.GetProperty("data")
                                 .GetProperty("sessionKey")
                                 .GetString() ?? "";

            await _api.SubscribeChatAsync(_accessToken, sessionKey);
            Log?.Invoke("채팅 읽기 준비 완료");
        }
    }

    public async Task DisconnectAsync()
    {
        if (_socket is null)
            return;

        try
        {
            if (_socket.Connected)
                await _socket.DisconnectAsync();
        }
        catch
        {
        }

        _socket.Dispose();
        _socket = null;
        ConnectionChanged?.Invoke(false);
    }

    private static ChatEvent ParseChat(JsonElement data)
    {
        var profile = data.TryGetProperty("profile", out var p)
            ? p
            : default;

        return new ChatEvent
        {
            ChannelId = Get(data, "channelId"),
            SenderChannelId = Get(data, "senderChannelId"),
            Content = Get(data, "content"),
            Nickname = profile.ValueKind == JsonValueKind.Object
                ? Get(profile, "nickname")
                : "",
            UserRoleCode = profile.ValueKind == JsonValueKind.Object
                ? Get(profile, "userRoleCode")
                : "",
            MessageTime = data.TryGetProperty("messageTime", out var time)
                          && time.TryGetInt64(out var v)
                ? v
                : 0
        };
    }

    private static string Get(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value)
            ? value.GetString() ?? ""
            : "";
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
