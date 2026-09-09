using YazmaBackup.ControlPlane;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class FrontendContentSecurityPolicyTests
{
    [Fact]
    public void Missing_web_root_still_produces_fail_closed_policy()
    {
        var policy = FrontendContentSecurityPolicy.Build(null);

        Assert.Contains("default-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_index_file_does_not_crash_policy_generation()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), "YazmaBackup.Tests", Guid.NewGuid().ToString("N"));

        var policy = FrontendContentSecurityPolicy.Build(missingDirectory);

        Assert.Contains("script-src 'self'", policy, StringComparison.Ordinal);
    }
}
