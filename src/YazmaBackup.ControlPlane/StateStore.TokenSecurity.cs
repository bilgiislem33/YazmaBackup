using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Contracts;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed partial class StateStore
{
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static bool FixedTimeEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}
