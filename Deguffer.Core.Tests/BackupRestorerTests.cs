using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Restoring a backup (§7.3): never over an entry that is there, never a file that names no entry,
/// and checked afterwards by reading the entry back.
/// </summary>
public sealed class BackupRestorerTests
{
    private readonly FakeUninstallRegistry _registry = new();

    private readonly FakeProcessRunner _runner = new();

    private BackupRestorer Restorer => new(_registry, new RegistryBackups(_runner, @"C:\backups", "reg.exe", TimeProvider.System));

    private static RegistryBackupFile Backup(UninstallScope scope) =>
        new(@"C:\backups\tool.reg", new UninstallKey(scope, "Tool").PhysicalPath, "Tool", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task ABackupOfAnAbsentEntryIsImportedAndCheckedBack()
    {
        var backup = Backup(UninstallScope.CurrentUser);
        _runner.Replying(_ =>
        {
            _registry.With(UninstallScope.CurrentUser, "Tool", ("DisplayName", "Tool"));
            return null;
        });

        var outcome = await Restorer.RestoreAsync(backup, isElevated: false, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(@"import ""C:\backups\tool.reg"" /reg:64", Assert.Single(_runner.Invocations).Arguments);
    }

    [Fact]
    public async Task AnImportThatLeavesNoEntryIsAFailedRestore()
    {
        var outcome = await Restorer.RestoreAsync(Backup(UninstallScope.CurrentUser), isElevated: false, CancellationToken.None);

        Assert.False(outcome.Succeeded);
    }

    /// <summary>An import merges, and a merge into a live entry is not a restore.</summary>
    [Fact]
    public async Task AnEntryThatIsThereIsNotImportedOver()
    {
        _registry.With(UninstallScope.CurrentUser, "Tool", ("DisplayName", "Tool"));

        var outcome = await Restorer.RestoreAsync(Backup(UninstallScope.CurrentUser), isElevated: false, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Empty(_runner.Invocations);
    }

    [Fact]
    public void AnEntryWindowsWillNotDescribeIsNotImportedOver()
    {
        _registry.Refusing(UninstallScope.CurrentUser);

        Assert.False(Restorer.MayRestore(Backup(UninstallScope.CurrentUser), isElevated: false).IsAllowed);
    }

    [Fact]
    public void AMachineWideBackupNeedsElevation()
    {
        var verdict = Restorer.MayRestore(Backup(UninstallScope.Machine32), isElevated: false);

        Assert.False(verdict.IsAllowed);
        Assert.True(verdict.NeedsElevation);
        Assert.True(Restorer.MayRestore(Backup(UninstallScope.Machine32), isElevated: true).IsAllowed);
    }

    [Fact]
    public void AFileThatNamesNoEntryIsNeverImported()
    {
        var other = new RegistryBackupFile(@"C:\x.reg", @"HKEY_CURRENT_USER\Software\Other", null, DateTimeOffset.UnixEpoch);

        Assert.False(Restorer.MayRestore(other, isElevated: true).IsAllowed);
    }
}
