using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace YazmaBackup.ControlPlane.Tests;

[CollectionDefinition("ControlPlane environment", DisableParallelization = true)]
public sealed class ControlPlaneEnvironmentFixture;

internal sealed class StateStoreTestScope : IDisposable
{
    private readonly Dictionary<string, string?> _saved = new(StringComparer.Ordinal);
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "yb-state-tests-" + Guid.NewGuid().ToString("N"));
    internal string StatePath => Path.Combine(Root, "control-plane-state.json");

    internal StateStoreTestScope()
    {
        Directory.CreateDirectory(Root);
        Set("YAZMABACKUP_STATE_DIR", Root);
        Set("YAZMABACKUP_STATE_ENGINE", "file");
        Set("YAZMABACKUP_HA_ROLE", "single");
        Set("YAZMABACKUP_DP_CERT_THUMBPRINT", null);
        Set("YAZMABACKUP_ENABLE_LEGACY_ADMIN_KEY", null);
    }

    internal StateStore OpenStore() => new(new TestEnvironment { ContentRootPath = Root });

    private void Set(string name, string? value)
    {
        _saved.Add(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var item in _saved) Environment.SetEnvironmentVariable(item.Key, item.Value);
        Directory.Delete(Root, recursive: true);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "YazmaBackup.ControlPlane.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
