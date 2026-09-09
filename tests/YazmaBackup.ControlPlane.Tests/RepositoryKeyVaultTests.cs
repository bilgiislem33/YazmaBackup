using System.Security.Cryptography;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class RepositoryKeyVaultTests
{
    [Fact]
    public void Key_identifier_is_stable_and_bound_to_key_material()
    {
        var first = RandomNumberGenerator.GetBytes(32);
        var second = RandomNumberGenerator.GetBytes(32);
        try
        {
            var firstId = RepositoryKeyVault.BuildKeyId(first);

            Assert.Equal(firstId, RepositoryKeyVault.BuildKeyId(first));
            Assert.NotEqual(firstId, RepositoryKeyVault.BuildKeyId(second));
            Assert.Matches("^auto-[0-9a-f]{16}$", firstId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(second);
        }
    }
}
