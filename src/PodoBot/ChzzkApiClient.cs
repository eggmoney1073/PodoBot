using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PodoBot;

public sealed class ChzzkApiClient
{
    private const string OpenApiBase = "https://openapi.chzzk.naver.com";

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public string BuildAuthorizationUrl(string state)
    {
        return "https://chzzk.naver.com/account-interlock"
               + $"?clientId={Uri.EscapeDataString(EmbeddedChzzkConfig.ClientId)}"
               + $"&redirectUri={Uri.EscapeDataString(EmbeddedChzzkConfig.RedirectUri)}"
               + $"&state={Uri.EscapeDataString(state)}";
    }

    public async Task<TokenResult> ExchangeCodeAsync(
        string code,
        string state,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            grantType = "authorization_code",
            clientId = EmbeddedChzzkConfig.ClientId,
            clientSecret = EmbeddedChzzkConfig.ClientSecret,
            code,
            state
        };

        using var response = await _http.PostAsJsonAsync(
            $"{OpenApiBase}/auth/v1/token",
            body,
            cancellationToken);

        return await ParseTokenAsync(response, cancellationToken);
    }

    public async Task<TokenResult> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            grantType = "refresh_token",
            refreshToken,
            clientId = EmbeddedChzzkConfig.ClientId,
            clientSecret = EmbeddedChzzkConfig.ClientSecret
        };

        using var response = await _http.PostAsJsonAsync(
            $"{OpenApiBase}/auth/v1/token",
            body,
            cancellationToken);

        return await ParseTokenAsync(response, cancellationToken);
    }

    public async Task<MeResult> GetMeAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = Bearer(
            HttpMethod.Get,
            $"{OpenApiBase}/open/v1/users/me",
            accessToken);

        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));

        var content = doc.RootElement.GetProperty("content");

        return new MeResult(
            content.GetProperty("channelId").GetString() ?? "",
            content.GetProperty("channelName").GetString() ?? "");
    }

    public async Task<string> CreateSessionUrlAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = Bearer(
            HttpMethod.Get,
            $"{OpenApiBase}/open/v1/sessions/auth",
            accessToken);

        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));

        return doc.RootElement
                   .GetProperty("content")
                   .GetProperty("url")
                   .GetString()
               ?? throw new InvalidOperationException("치지직 세션 주소를 받지 못했습니다.");
    }

    public async Task SubscribeChatAsync(
        string accessToken,
        string sessionKey,
        CancellationToken cancellationToken = default)
    {
        var url = $"{OpenApiBase}/open/v1/sessions/events/subscribe/chat"
                  + $"?sessionKey={Uri.EscapeDataString(sessionKey)}";

        using var request = Bearer(HttpMethod.Post, url, accessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SendChatAsync(
        string accessToken,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (message.Length > 100)
            message = message[..100];

        using var request = Bearer(
            HttpMethod.Post,
            $"{OpenApiBase}/open/v1/chats/send",
            accessToken);

        request.Content = JsonContent.Create(new { message });

        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static HttpRequestMessage Bearer(
        HttpMethod method,
        string url,
        string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        return request;
    }

    private static async Task<TokenResult> ParseTokenAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);

        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));

        var content = doc.RootElement.GetProperty("content");
        var access = content.GetProperty("accessToken").GetString() ?? "";
        var refresh = content.GetProperty("refreshToken").GetString() ?? "";

        var expiresText = content.GetProperty("expiresIn").ToString();
        if (!int.TryParse(expiresText, out var expires))
            expires = 86400;

        return new TokenResult(access, refresh, expires);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"치지직 API 오류 {(int)response.StatusCode}: {body}");
    }
}
