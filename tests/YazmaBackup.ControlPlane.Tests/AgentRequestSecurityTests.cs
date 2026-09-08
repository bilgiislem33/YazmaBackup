using System.Security.Cryptography;
using YazmaBackup.ControlPlane.Endpoints;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class AgentRequestSecurityTests
{
    [Theory]
    [InlineData("PC-01", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Machine_validation_is_fail_closed(string? value, bool expected) =>
        Assert.Equal(expected, AgentRequestSecurity.ValidMachine(value));

    [Theory]
    [InlineData("repo-01", true)]
    [InlineData("repo_01.prod", true)]
    [InlineData("repo/share", false)]
    [InlineData("repo 01", false)]
    public void Repository_identifier_accepts_only_safe_characters(string value, bool expected) =>
        Assert.Equal(expected, AgentRequestSecurity.ValidIdentifier(value, 128));

    [Fact]
    public void Capabilities_are_trimmed_deduplicated_and_sorted()
    {
        var actual = AgentRequestSecurity.SanitizeCapabilities([" restore ", "backup", "backup", "", "telemetry"]);
        Assert.Equal(["backup", "restore", "telemetry"], actual);
    }

    [Fact]
    public void Public_key_requires_at_least_3072_bit_rsa()
    {
        using var weak = RSA.Create(2048);
        using var strong = RSA.Create(3072);

        Assert.False(AgentRequestSecurity.ValidPublicKey(weak.ExportRSAPublicKeyPem()));
        Assert.True(AgentRequestSecurity.ValidPublicKey(strong.ExportRSAPublicKeyPem()));
    }
}
