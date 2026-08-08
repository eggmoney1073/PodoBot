using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PodoBot;

public sealed class SecureTokenStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public SecureTokenStore(string directory)
    {
        _path = Path.Combine(directory, "auth.dat");
    }

    public AuthTokens Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new AuthTokens();

            var encrypted = File.ReadAllBytes(_path);
            var plain = ProtectedData.Unprotect(
                encrypted,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize<AuthTokens>(
                Encoding.UTF8.GetString(plain),
                _json) ?? new AuthTokens();
        }
        catch
        {
            return new AuthTokens();
        }
    }

    public void Save(AuthTokens tokens)
    {
        var plain = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(tokens, _json));

        var encrypted = ProtectedData.Protect(
            plain,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        File.WriteAllBytes(_path, encrypted);
    }

    public void Clear()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
