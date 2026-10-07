using System.Text;
using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Removing stale entries (§7.3): only what the machine still proves stale at the moment of
/// deletion goes, a failed backup stops the removal, and §5.6 checks that everything beside the
/// removed entries survived.
/// </summary>
public sealed partial class EntryRemoverTests : IDisposable
{
    private const string RegExe = @"C:\Windows\System32\reg.exe";

    private readonly TempDirectory _temp = new();

    private readonly FakeUninstallRegistry _registry = new();

    private readonly FakeWindowsInstaller _installer = new();

    private readonly FakePathProbe _paths = new();

    /// <summary>A file the probe reports standing, named under the test's tree.</summary>
    private string StandingFile(string folder, string name)
    {
        var path = Path.Combine(_temp.Path, folder, name);
        _paths.File(path);
        return path;
    }

    private readonly FakeProcessRunner _runner = new();

    private readonly RegistryBackups _backups;

    public EntryRemoverTests()
    {
        _backups = new RegistryBackups(_runner, Path.Combine(_temp.Path, "backups"), RegExe, TimeProvider.System);

        // reg.exe, as far as a removal can see it: a file appears where it was told to write one.
        _runner.Replying(RegExe, arguments =>
        {
            if (ExportTarget().Match(arguments) is { Success: true } target)
            {
                File.WriteAllText(target.Groups["file"].Value, "Windows Registry Editor Version 5.00", Encoding.Unicode);
            }

            return null;
        });
    }

    public void Dispose() => _temp.Dispose();

    [GeneratedRegex("^export \"[^\"]+\" \"(?<file>[^\"]+)\"")]
    private static partial Regex ExportTarget();

    private UninstallKey Stale(UninstallScope scope, string name) => _registry.With(scope, name,
        ("DisplayName", name), ("UninstallString", $"\"{Path.Combine(_temp.Path, name, "unins000.exe")}\""));

    private InstalledAppsReader Reader => new(_registry, _installer, _paths, FixedSystemDirectories.Standard, new FakePackageDependencies(), new FakeVolumeInventory());

    private InstalledEntry Current(UninstallKey key) => Reader.ReadAgain(key)!;

    private Task<EntryRemovalReport> Remove(bool backUp, bool isElevated, params UninstallKey[] keys) =>
        new EntryRemover(_registry, Reader, _backups).RemoveAsync([.. keys.Select(Current)], backUp, isElevated, CancellationToken.None);

    [Fact]
    public async Task AStaleEntryIsBackedUpRemovedAndItsNeighboursSurvive()
    {
        var gone = Stale(UninstallScope.CurrentUser, "Gone");
        var neighbour = _registry.With(UninstallScope.CurrentUser, "Neighbour", ("DisplayName", "Neighbour"));

        var report = await Remove(backUp: true, isElevated: false, gone);

        var item = Assert.Single(report.Removed);
        Assert.NotNull(item.BackupPath);
        Assert.True(File.Exists(item.BackupPath));
        Assert.Equal([gone], _registry.Deleted);
        Assert.Equal(PathPresence.Present, _registry.ReadOne(neighbour).Presence);
        Assert.True(report.Verification.Passed);
        Assert.Contains(report.Verification.Checks, c => c.Outcome == VerificationOutcome.Survived && c.Subject == neighbour.PhysicalPath);
        Assert.Equal(2, report.Verification.Checks.Count);
    }

    [Fact]
    public async Task TheBackupNamesTheKeyByItsPhysicalPathInTheSixtyFourBitView()
    {
        var gone = Stale(UninstallScope.Machine32, "Gone");

        await Remove(backUp: true, isElevated: true, gone);

        var (_, arguments) = Assert.Single(_runner.Invocations);
        Assert.StartsWith(@"export ""HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Gone"" ", arguments, StringComparison.Ordinal);
        Assert.EndsWith(" /y /reg:64", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEntryWhoseBackupFailsIsNotRemoved()
    {
        var gone = Stale(UninstallScope.CurrentUser, "Gone");
        _runner.Replying(RegExe, _ => new CommandOutcome(1, string.Empty, "ERROR: Access is denied."));

        var report = await Remove(backUp: true, isElevated: false, gone);

        Assert.Empty(report.Removed);
        Assert.Empty(_registry.Deleted);
        Assert.Contains("was not removed", Assert.Single(report.NotRemoved).Message, StringComparison.Ordinal);
    }

    /// <summary>reg.exe exiting 0 without writing the file is not a backup.</summary>
    [Fact]
    public async Task ABackupThatWroteNoFileIsAFailedBackup()
    {
        var gone = Stale(UninstallScope.CurrentUser, "Gone");
        _runner.Replying(RegExe, _ => new CommandOutcome(0, string.Empty, string.Empty));

        var report = await Remove(backUp: true, isElevated: false, gone);

        Assert.Empty(_registry.Deleted);
        Assert.Empty(report.Removed);
    }

    [Fact]
    public async Task WithBackupsOffNothingRunsAndTheEntryGoes()
    {
        var gone = Stale(UninstallScope.CurrentUser, "Gone");
        var neighbour = _registry.With(UninstallScope.CurrentUser, "Neighbour", ("DisplayName", "Neighbour"));

        var report = await Remove(backUp: false, isElevated: false, gone);

        Assert.Empty(_runner.Invocations);
        Assert.Null(Assert.Single(report.Removed).BackupPath);
        Assert.Equal([gone], _registry.Deleted);
        Assert.Equal(PathPresence.Present, _registry.ReadOne(neighbour).Presence);
        Assert.True(report.Verification.Passed);
        Assert.Contains(report.Verification.Checks, c => c.Outcome == VerificationOutcome.Survived && c.Subject == neighbour.PhysicalPath);
    }

    [Fact]
    public async Task AnEntryThatChangedAfterItWasChosenIsLeft()
    {
        var gone = Stale(UninstallScope.CurrentUser, "Gone");
        var chosen = Current(gone);
        _registry.With(UninstallScope.CurrentUser, "Gone", ("DisplayName", "Gone 2"), ("UninstallString", chosen.Command.Text));

        var report = await new EntryRemover(_registry, Reader, _backups).RemoveAsync([chosen], false, false, CancellationToken.None);

        Assert.Empty(_registry.Deleted);
        Assert.Contains("changed after it was chosen", Assert.Single(report.NotRemoved).Message, StringComparison.Ordinal);
    }

    /// <summary>Decided twice: an uninstaller that reappears makes the entry installed again.</summary>
    [Fact]
    public async Task AnEntryNoLongerStaleAtTheMomentOfRemovalIsLeft()
    {
        var gone = Stale(UninstallScope.CurrentUser, "Gone");
        var chosen = Current(gone);
        StandingFile("Gone", "unins000.exe");

        var report = await new EntryRemover(_registry, Reader, _backups).RemoveAsync([chosen], false, false, CancellationToken.None);

        Assert.Empty(_registry.Deleted);
        Assert.Contains("proves is stale", Assert.Single(report.NotRemoved).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInstalledEntryIsNeverRemoved()
    {
        var uninstaller = StandingFile("Tool", "unins000.exe");
        var tool = _registry.With(UninstallScope.CurrentUser, "Tool", ("DisplayName", "Tool"), ("UninstallString", $"\"{uninstaller}\""));

        var report = await Remove(backUp: false, isElevated: true, tool);

        Assert.Empty(_registry.Deleted);
        Assert.Empty(report.Removed);
    }

    [Fact]
    public async Task AMachineWideEntryIsLeftWhenDegufferIsNotElevated()
    {
        var gone = Stale(UninstallScope.Machine64, "Gone");

        var report = await Remove(backUp: true, isElevated: false, gone);

        Assert.Empty(_registry.Deleted);
        Assert.Empty(_runner.Invocations);
        Assert.Contains("administrator", Assert.Single(report.NotRemoved).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedDeleteIsReportedWithItsBackupKept()
    {
        var gone = Stale(UninstallScope.Machine64, "Gone");
        _registry.RefusedDeletes.Add(gone);

        var report = await Remove(backUp: true, isElevated: true, gone);

        var item = Assert.Single(report.NotRemoved);
        Assert.StartsWith("Windows refused", item.Message, StringComparison.Ordinal);
        Assert.NotNull(item.BackupPath);
    }

    /// <summary>§5.6: an entry beside the removed one that went missing fails the run and is named.</summary>
    [Fact]
    public async Task ANeighbourThatWentMissingFailsTheCheck()
    {
        var gone = Stale(UninstallScope.CurrentUser, "Gone");
        var neighbour = _registry.With(UninstallScope.CurrentUser, "Neighbour", ("DisplayName", "Neighbour"));
        _registry.AfterDelete = _ => _registry.Remove(neighbour);

        var report = await Remove(backUp: false, isElevated: false, gone);

        Assert.False(report.Verification.Passed);
        Assert.Equal(neighbour.PhysicalPath, Assert.Single(report.Verification.Failures).Subject);
    }

    [Fact]
    public async Task AnUninstallKeyThatWillNotOpenAfterwardsIsUnverifiedRatherThanPassed()
    {
        var gone = Stale(UninstallScope.CurrentUser, "Gone");
        _registry.AfterDelete = _ => _registry.Refusing(UninstallScope.CurrentUser);

        var report = await Remove(backUp: false, isElevated: false, gone);

        Assert.Contains(report.Verification.Checks, c => c.Outcome == VerificationOutcome.Unverified);
        Assert.False(report.Verification.Passed);
    }

    [Fact]
    public async Task AnEntryAlreadyGoneIsReportedAndNothingIsDeleted()
    {
        var gone = Stale(UninstallScope.CurrentUser, "Gone");
        var chosen = Current(gone);
        _registry.Remove(gone);

        var report = await new EntryRemover(_registry, Reader, _backups).RemoveAsync([chosen], false, false, CancellationToken.None);

        Assert.Empty(_registry.Deleted);
        Assert.Contains("already gone", Assert.Single(report.NotRemoved).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledRemovalReachesNoFurtherEntry()
    {
        var first = Stale(UninstallScope.CurrentUser, "First");
        var second = Stale(UninstallScope.CurrentUser, "Second");
        using var cancel = new CancellationTokenSource();
        _registry.AfterDelete = _ => cancel.Cancel();

        var report = await new EntryRemover(_registry, Reader, _backups)
            .RemoveAsync([Current(first), Current(second)], false, false, cancel.Token);

        Assert.Equal([first], _registry.Deleted);
        Assert.StartsWith("Not reached", Assert.Single(report.NotRemoved).Message, StringComparison.Ordinal);
    }
}
