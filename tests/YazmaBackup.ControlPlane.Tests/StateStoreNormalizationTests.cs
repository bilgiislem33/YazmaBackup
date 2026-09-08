using System.Reflection;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane.Tests;

public sealed class StateStoreNormalizationTests
{
    [Fact]
    public void Reload_normalization_preserves_business_dependencies_and_disaster_recovery_sessions()
    {
        var dependency = new BusinessServiceDependencyRecord(
            Guid.NewGuid(),
            "erp",
            "database",
            "hard",
            true,
            DateTimeOffset.UtcNow);
        var step = new DisasterRecoverySessionStepRecord(
            1,
            "database",
            "Veritabanı",
            [],
            "approved",
            DateTimeOffset.UtcNow,
            null,
            null);
        var session = new DisasterRecoverySessionRecord(
            Guid.NewGuid(),
            "Üretim DR",
            "active",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            1,
            [step]);
        var loaded = new StateStore.StateDocument
        {
            BusinessServiceDependencies = new Dictionary<Guid, BusinessServiceDependencyRecord>
            {
                [dependency.DependencyId] = dependency
            },
            DisasterRecoverySessions = new Dictionary<Guid, DisasterRecoverySessionRecord>
            {
                [session.SessionId] = session
            }
        };

        var normalize = typeof(StateStore).GetMethod(
            "NormalizeLoadedState",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(normalize);
        var normalized = Assert.IsType<StateStore.StateDocument>(normalize.Invoke(null, [loaded]));
        Assert.Equal(dependency, Assert.Single(normalized.BusinessServiceDependencies).Value);
        Assert.Equal(session, Assert.Single(normalized.DisasterRecoverySessions).Value);
    }
}
