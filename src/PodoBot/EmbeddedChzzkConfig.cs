namespace PodoBot;

internal static class EmbeddedChzzkConfig
{
    // GitHub Actions overwrites this file during the private release build.
    public const string ClientId = "";
    public const string ClientSecret = "";
    public const string RedirectUri = "http://localhost:18766/auth/callback";

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
