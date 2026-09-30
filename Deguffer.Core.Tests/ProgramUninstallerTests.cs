using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Running an installed program's own uninstaller (§7.3): what is run for each kind of entry, what is
/// refused, and that the report states what the list says afterwards rather than claiming anything.
/// </summary>
public sealed class ProgramUninstallerTests : IDisposable
{
    private const string Msiexec = @"C:\Windows\System32\msiexec.exe";

    private const string ProductKey = "{12345678-9ABC-DEF0-1234-56789ABCDEF0}";

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

    private string StandingFolder(string folder)
    {
        var path = Path.Combine(_temp.Path, folder);
        _paths.Directory(path);
        return path;
    }

    private readonly FakeUninstallLauncher _launcher = new();

    public void Dispose() => _temp.Dispose();

    private InstalledAppsReader Reader => new(_registry, _installer, _paths, FixedSystemDirectories.Standard, new FakePackageDependencies());

    private ProgramUninstaller Uninstaller => new(Reader, _launcher, Msiexec);

    private InstalledEntry Current(UninstallKey key) => Reader.ReadAgain(key)!;

    private (ActionVerdict Verdict, UninstallLaunch? Launch) Policy(UninstallKey key) =>
        UninstallPolicy.MayUninstall(Current(key), Msiexec);

    private UninstallKey Installed(string name, string arguments = "/uninstall")
    {
        var uninstaller = StandingFile(name, "setup.exe");
        return _registry.With(UninstallScope.Machine64, name, ("DisplayName", name), ("UninstallString", $"\"{uninstaller}\" {arguments}"));
    }

    [Fact]
    public void AProgramRunsTheCommandItsEntryRecords()
    {
        var key = Installed("Tool");

        var (verdict, launch) = Policy(key);

        Assert.True(verdict.IsAllowed);
        Assert.Equal(new UninstallLaunch(Path.Combine(_temp.Path, "Tool", "setup.exe"), "/uninstall"), launch);
    }

    /// <summary>Removed by code, never by <c>/I</c>, which opens a maintenance dialog.</summary>
    [Fact]
    public void AWindowsInstallerProductIsRemovedByItsCode()
    {
        _installer.With(Guid.Parse(ProductKey), InstallerProductState.Installed);
        var key = _registry.With(UninstallScope.Machine64, ProductKey,
            ("DisplayName", "Tool"), ("WindowsInstaller", 1), ("UninstallString", $"MsiExec.exe /I{ProductKey}"));

        var (_, launch) = Policy(key);

        Assert.Equal(new UninstallLaunch(Msiexec, $"/x {ProductKey.ToLowerInvariant()}"), launch);
    }

    [Fact]
    public void ABareNameIsRunForTheShellToResolve()
    {
        var key = _registry.With(UninstallScope.CurrentUser, "Tool", ("DisplayName", "Tool"), ("UninstallString", "winget uninstall --id Tool"));

        Assert.Equal(new UninstallLaunch("winget", "uninstall --id Tool"), Policy(key).Launch);
    }

    [Fact]
    public void AStaleEntryIsNotUninstalledButRemoved()
    {
        var key = _registry.With(UninstallScope.CurrentUser, "Gone",
            ("DisplayName", "Gone"), ("UninstallString", $"\"{Path.Combine(_temp.Path, "Gone", "setup.exe")}\""));

        var (verdict, launch) = Policy(key);

        Assert.False(verdict.IsAllowed);
        Assert.Null(launch);
        Assert.Contains("Remove the entry instead", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryThatSetsNoRemoveIsRefused()
    {
        var uninstaller = StandingFile("Tool", "setup.exe");
        var key = _registry.With(UninstallScope.CurrentUser, "Tool", ("DisplayName", "Tool"), ("NoRemove", 1), ("UninstallString", $"\"{uninstaller}\""));

        Assert.False(Policy(key).Verdict.IsAllowed);
    }

    [Fact]
    public void AnotherAccountsProductIsRefused()
    {
        _installer.With(Guid.Parse(ProductKey), InstallerProductState.OtherAccount);
        var key = _registry.With(UninstallScope.Machine64, ProductKey, ("DisplayName", "Theirs"), ("WindowsInstaller", 1));

        Assert.False(Policy(key).Verdict.IsAllowed);
    }

    [Fact]
    public void AnEntryWithNoCommandIsRefused()
    {
        var key = _registry.With(UninstallScope.CurrentUser, "Tool", ("DisplayName", "Tool"), ("InstallLocation", StandingFolder("Tool")));

        Assert.False(Policy(key).Verdict.IsAllowed);
    }

    [Fact]
    public async Task AnEntryTheUninstallerRemovedIsReportedGone()
    {
        var key = Installed("Tool");
        var chosen = Current(key);
        var (_, launch, _) = Uninstaller.Prepare(chosen);
        _launcher.WhileRunning = _ => _registry.Remove(key);

        var report = await Uninstaller.UninstallAsync(chosen, launch!, CancellationToken.None);

        Assert.Equal(AfterUninstall.EntryGone, report.After);
        Assert.Single(_launcher.Started);
    }

    [Fact]
    public async Task AnEntryLeftBehindWithItsProgramGoneIsReportedNowStale()
    {
        var key = Installed("Tool");
        var chosen = Current(key);
        var (_, launch, _) = Uninstaller.Prepare(chosen);
        _launcher.WhileRunning = l => _paths.Remove(l.FileName);

        var report = await Uninstaller.UninstallAsync(chosen, launch!, CancellationToken.None);

        Assert.Equal(AfterUninstall.NowStale, report.After);
        Assert.Contains("can now be removed", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEntryStillInstalledSaysSoWithoutClaimingFailure()
    {
        var key = Installed("Tool");
        var chosen = Current(key);
        var (_, launch, _) = Uninstaller.Prepare(chosen);

        var report = await Uninstaller.UninstallAsync(chosen, launch!, CancellationToken.None);

        Assert.Equal(AfterUninstall.StillInstalled, report.After);
        Assert.Contains("finish after they exit", report.Summary, StringComparison.Ordinal);
    }

    /// <summary>The command confirmed is the command that runs.</summary>
    [Fact]
    public async Task ACommandThatChangedAfterConfirmationIsNotRun()
    {
        var key = Installed("Tool");
        var chosen = Current(key);
        var (_, launch, _) = Uninstaller.Prepare(chosen);
        Installed("Tool", "/other");

        var report = await Uninstaller.UninstallAsync(chosen, launch!, CancellationToken.None);

        Assert.Equal(AfterUninstall.NotRun, report.After);
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public async Task AnUninstallerThatDidNotStartReportsWhy()
    {
        var key = Installed("Tool");
        var chosen = Current(key);
        var (_, launch, _) = Uninstaller.Prepare(chosen);
        _launcher.Outcome = new LaunchOutcome(false, "The administrator prompt was declined.");

        var report = await Uninstaller.UninstallAsync(chosen, launch!, CancellationToken.None);

        Assert.Equal(AfterUninstall.NotRun, report.After);
        Assert.Equal("The administrator prompt was declined.", report.Summary);
    }

    [Fact]
    public void TheConfirmationNamesTheCommand()
    {
        var key = Installed("Tool");
        var (_, launch) = Policy(key);

        var prompt = InstalledAppsPrompt.ForUninstall(Current(key), launch!);

        Assert.Equal([launch!.Display], prompt.Items);
        Assert.Equal("Uninstall 'Tool'?", prompt.Title);
    }

    [Fact]
    public void ARefusedUninstallerIsNotRun()
    {
        var entry = Current(Installed("Tool")) with
        {
            Command = new ProgramCommand("x", @"C:\x.exe", string.Empty, PathPresence.Refused),
            Standing = new StandingVerdict(EntryStanding.Unproven, "Because."),
        };

        Assert.False(UninstallPolicy.MayUninstall(entry, Msiexec).Verdict.IsAllowed);
    }
}
