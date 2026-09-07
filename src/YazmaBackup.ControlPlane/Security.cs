using System.Security.Cryptography;
using System.Text;

namespace YazmaBackup.ControlPlane;

public static class Security
{
    public static string RequireSecret(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) || value.Length < 24)
            throw new InvalidOperationException($"{name} must be set and at least 24 characters.");
        return value;
    }

    public static bool ConstantTimeEquals(string? supplied, string expected)
    {
        if (string.IsNullOrEmpty(supplied)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
