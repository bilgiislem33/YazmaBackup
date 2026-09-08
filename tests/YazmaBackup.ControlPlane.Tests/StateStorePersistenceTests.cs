using System.Text.Json;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane.Tests;

[Collection("ControlPlane environment")]
public sealed class StateStorePersistenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Dependencies_and_approved_DR_steps_survive_reopen_and_unrelated_commits()
    {
        using var scope = new StateStoreTestScope();
        Guid sessionId;
        Guid dependencyId;
        using (var store = scope.OpenStore())
        {
            var dependency = await store.UpsertDependencyAsync("erp", "database", "hard", true, CancellationToken.None);
            dependencyId = dependency.DependencyId;
            var session = await store.CreateDisasterRecoverySessionAsync("Recovery exercise",
                [new(1, "database", "Database", [], "pending", null, null, null),
                 new(2, "erp", "ERP", ["database"], "pending", null, null, null)], CancellationToken.None);
            sessionId = session.SessionId;
            await store.ApproveDisasterRecoveryStepAsync(sessionId, 1, CancellationToken.None);
        }
        using (var store = scope.OpenStore())
        {
            Assert.Equal(dependencyId, Assert.Single(await store.GetDependenciesAsync(CancellationToken.None)).DependencyId);
            var session = Assert.Single(await store.GetDisasterRecoverySessionsAsync(CancellationToken.None));
            Assert.Equal("approved", session.Steps[0].Status);
            Assert.NotNull(session.Steps[0].ApprovedAtUtc);
            await store.CreateEnrollmentGrantAsync(TimeSpan.FromMinutes(10), 1, CancellationToken.None);
            var updated = await store.VerifyDisasterRecoveryStepAsync(sessionId, 1, "Database verified", CancellationToken.None);
            Assert.Equal(2, updated.CurrentOrder);
        }
        using (var store = scope.OpenStore())
        {
            Assert.Equal(dependencyId, Assert.Single(await store.GetDependenciesAsync(CancellationToken.None)).DependencyId);
            var session = Assert.Single(await store.GetDisasterRecoverySessionsAsync(CancellationToken.None));
            Assert.Equal(2, session.CurrentOrder);
            Assert.Equal("verified", session.Steps[0].Status);
            Assert.Equal("Database verified", session.Steps[0].VerificationNote);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.VerifyDisasterRecoveryStepAsync(sessionId, 2, "Not approved", CancellationToken.None));
            await store.ApproveDisasterRecoveryStepAsync(sessionId, 2, CancellationToken.None);
            await store.VerifyDisasterRecoveryStepAsync(sessionId, 2, "ERP verified", CancellationToken.None);
        }
        using (var store = scope.OpenStore())
        {
            var session = Assert.Single(await store.GetDisasterRecoverySessionsAsync(CancellationToken.None));
            Assert.Equal("completed", session.Status);
            Assert.NotNull(session.CompletedAtUtc);
            Assert.All(session.Steps, step => Assert.Equal("verified", step.Status));
        }
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    public async Task Corrupt_primary_loads_valid_backup_without_overwriting_it(string corruptJson)
    {
        using var scope = new StateStoreTestScope();
        using (var store = scope.OpenStore())
        {
            await store.UpsertDependencyAsync("erp", "database", "hard", true, CancellationToken.None);
            await store.CreateEnrollmentGrantAsync(TimeSpan.FromMinutes(10), 1, CancellationToken.None);
        }
        var backup = await File.ReadAllTextAsync(scope.StatePath + ".bak");
        await File.WriteAllTextAsync(scope.StatePath, corruptJson);
        using (var store = scope.OpenStore())
        {
            Assert.Single(await store.GetDependenciesAsync(CancellationToken.None));
        }
        Assert.Equal(backup, await File.ReadAllTextAsync(scope.StatePath + ".bak"));
        Assert.Equal(corruptJson, await File.ReadAllTextAsync(scope.StatePath));
    }

    [Fact]
    public async Task Null_state_without_backup_fails_instead_of_creating_an_empty_database()
    {
        using var scope = new StateStoreTestScope();
        await File.WriteAllTextAsync(scope.StatePath, "null");
        Assert.Throws<InvalidDataException>(() => { using var store = scope.OpenStore(); });
        Assert.Equal("null", await File.ReadAllTextAsync(scope.StatePath));
    }

    [Fact]
    public async Task Future_schema_is_not_replaced_with_an_older_backup()
    {
        using var scope = new StateStoreTestScope();
        var future = JsonSerializer.Serialize(new StateStore.StateDocument { SchemaVersion = "999" }, JsonOptions);
        await File.WriteAllTextAsync(scope.StatePath, future);
        await File.WriteAllTextAsync(scope.StatePath + ".bak", JsonSerializer.Serialize(new StateStore.StateDocument(), JsonOptions));
        var error = Record.Exception(() => { using var store = scope.OpenStore(); });
        Assert.NotNull(error);
        Assert.Contains("Downgrade is blocked", error.Message, StringComparison.Ordinal);
        Assert.Equal(future, await File.ReadAllTextAsync(scope.StatePath));
    }

    [Fact]
    public async Task Stale_writer_conflicts_and_retry_keeps_both_changes_after_reload()
    {
        using var scope = new StateStoreTestScope();
        using (var first = scope.OpenStore())
        using (var second = scope.OpenStore())
        {
            await first.UpsertDependencyAsync("erp", "database", "hard", true, CancellationToken.None);
            await Assert.ThrowsAsync<DistributedStateConflictException>(() =>
                second.UpsertDependencyAsync("mail", "directory", "hard", true, CancellationToken.None));
            await second.UpsertDependencyAsync("mail", "directory", "hard", true, CancellationToken.None);
        }
        using var reopened = scope.OpenStore();
        var dependencies = await reopened.GetDependenciesAsync(CancellationToken.None);
        Assert.Equal(2, dependencies.Count);
        Assert.Contains(dependencies, item => item.ServiceId == "erp");
        Assert.Contains(dependencies, item => item.ServiceId == "mail");
    }
}
