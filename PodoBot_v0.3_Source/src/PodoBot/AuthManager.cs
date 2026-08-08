using System.Security.Cryptography;
using System.Text;

namespace PodoBot;

public sealed class AuthManager
{
    private readonly ChzzkApiClient _api;
    private readonly SecureTokenStore _tokenStore;
    private AuthTokens _tokens;

    public string PendingState { get; private set; } = "";
    public AuthTokens Tokens => _tokens;

    public AuthManager(ChzzkApiClient api, SecureTokenStore tokenStore)
    {
        _api = api;
        _tokenStore = tokenStore;
        _tokens = tokenStore.Load();
    }

    public string CreateLoginUrl()
    {
        if (!EmbeddedChzzkConfig.IsConfigured)
        {
            throw new InvalidOperationException(
                "이 빌드에는 치지직 앱 연결 정보가 들어있지 않습니다.");
        }

        PendingState = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(24));

        return _api.BuildAuthorizationUrl(PendingState);
    }

    public async Task HandleCodeAsync(string code, string state)
    {
        if (string.IsNullOrWhiteSpace(PendingState)
            || !FixedEquals(PendingState, state))
        {
            throw new InvalidOperationException("로그인 인증값이 일치하지 않습니다.");
        }

        var token = await _api.ExchangeCodeAsync(code, state);
        Apply(token);

        var me = await _api.GetMeAsync(_tokens.AccessToken);
        _tokens.ChannelId = me.ChannelId;
        _tokens.ChannelName = me.ChannelName;
        _tokenStore.Save(_tokens);
        PendingState = "";
    }

    public async Task<string> GetAccessTokenAsync()
    {
        if (!string.IsNullOrWhiteSpace(_tokens.AccessToken)
            && _tokens.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2))
        {
            return _tokens.AccessToken;
        }

        if (string.IsNullOrWhiteSpace(_tokens.RefreshToken))
            throw new InvalidOperationException("치지직 로그인이 필요합니다.");

        var token = await _api.RefreshAsync(_tokens.RefreshToken);
        Apply(token);
        _tokenStore.Save(_tokens);
        return _tokens.AccessToken;
    }

    public void Logout()
    {
        _tokens = new AuthTokens();
        _tokenStore.Clear();
    }

    private void Apply(TokenResult token)
    {
        _tokens.AccessToken = token.AccessToken;
        _tokens.RefreshToken = token.RefreshToken;
        _tokens.ExpiresAtUtc =
            DateTime.UtcNow.AddSeconds(Math.Max(60, token.ExpiresInSeconds));
    }

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);

        return a.Length == b.Length
               && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
