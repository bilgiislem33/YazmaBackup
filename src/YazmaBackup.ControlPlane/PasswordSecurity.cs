using System.Security.Cryptography;
using System.Text;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public static class PasswordSecurity
{
    public const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static void ValidateNewPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 14 || password.Length > 256)
            throw new ArgumentException("Password must be between 14 and 256 characters.", nameof(password));
    }

    public static (string HashBase64, string SaltBase64, int Iterations) HashPassword(string password)
    {
        ValidateNewPassword(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var hash = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
            try { return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), Iterations); }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public static bool Verify(string password, ManagementUserRecord user)
    {
        if (string.IsNullOrEmpty(password)) return false;
        byte[]? salt = null;
        byte[]? expected = null;
        byte[]? actual = null;
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            salt = Convert.FromBase64String(user.PasswordSaltBase64);
            expected = Convert.FromBase64String(user.PasswordHashBase64);
            if (salt.Length != SaltBytes || expected.Length != HashBytes || user.PasswordIterations < 100_000) return false;
            actual = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, user.PasswordIterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (salt is not null) CryptographicOperations.ZeroMemory(salt);
            if (expected is not null) CryptographicOperations.ZeroMemory(expected);
            if (actual is not null) CryptographicOperations.ZeroMemory(actual);
        }
    }

    public static void PerformDummyVerification(string password)
    {
        var salt = new byte[SaltBytes];
        var passwordBytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
        try
        {
            var hash = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
            CryptographicOperations.ZeroMemory(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(salt);
        }
    }
}
