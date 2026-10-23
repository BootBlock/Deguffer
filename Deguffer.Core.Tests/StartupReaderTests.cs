using Deguffer.Core.Safety;
using Deguffer.Core.Startup;
using Deguffer.Testing;
using Microsoft.Win32;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.5's list, read through a fake registry, real Startup folders in a scratch tree, and described
/// shortcuts, programs and packages, so no entry on this machine is read.
/// </summary>
public sealed class StartupReaderTests : IDisposable
{
    private const string Tool = @"C:\Program Files\Tool\tool.exe";
    private const string Agent = @"C:\Program Files (x86)\Agent\agent.exe";
    private const string Sync = @"C:\Users\testuser\AppData\Local\Sync\sync.exe";
    private const string Shared = @"C:\Program Files\Shared\shared.exe";

    private readonly TempDirectory _root = new();
    private readonly FakeUserEnvironment _user;
    private readonly FakeSystemDirectories _system;
    private readonly FakeStartupRegistry _registry = new();
    private readonly FakeShortcutReader _shortcuts = new();
    private readonly FakeProgramFiles _programs = new();

    public StartupReaderTests()
    {
        _user = new FakeUserEnvironment(_root.CreateDirectory("user"));
        _system = new FakeSystemDirectories(_root.CreateDirectory("system"));
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void AnEntryFromEachSourceIsListedOnceUnderTheSourceItCameFrom()
    {
        _registry
            .WithRun(StartupSource.UserRun, "Sync", $"\"{Sync}\" --background")
            .WithRun(StartupSource.MachineRun, "Tool", Tool)
            .WithRun(StartupSource.MachineRun32, "Agent", $"\"{Agent}\"");
        _programs.With(Sync).With(Tool).With(Agent).With(Shared).With(@"C:\Program Files\WindowsApps\Pkg\App.exe");
        UserShortcut("Notes.lnk", new ShortcutTarget(Sync, "--notes"));
        CommonShortcut("Shared.lnk", new ShortcutTarget(Shared, string.Empty));

        var reading = Reader(new PackagedTasksRead(true,
            [new PackagedTask("Pkg_1234567890abc", "AppStartup", "Packaged app", "Contoso", @"C:\Program Files\WindowsApps\Pkg\App.exe", 2)]))
            .Read(CancellationToken.None);

        Assert.Equal(
            [
                (StartupSource.UserRun, "Sync"),
                (StartupSource.MachineRun, "Tool"),
                (StartupSource.MachineRun32, "Agent"),
                (StartupSource.UserStartupFolder, "Notes.lnk"),
                (StartupSource.CommonStartupFolder, "Shared.lnk"),
                (StartupSource.PackagedTask, "AppStartup"),
            ],
            reading.Entries.Select(e => (e.Key.Source, e.Key.Name)).OrderBy(k => k.Source));

        Assert.Equal(Sync, reading.Entries.Single(e => e.Key.Name == "Sync").Program);
        Assert.Equal(Tool, reading.Entries.Single(e => e.Key.Name == "Tool").Program);
        Assert.Equal(Sync, reading.Entries.Single(e => e.Key.Name == "Notes.lnk").Program);
        Assert.Empty(reading.Notes);
    }

    [Fact]
    public void ADisabledEntryShowsAsDisabledWhicheverOfSettingsOrTaskManagerDisabledIt()
    {
        _registry
            .WithRun(StartupSource.UserRun, "ByTaskManager", Tool)
            .WithRun(StartupSource.UserRun, "BySettings", Tool)
            .WithRun(StartupSource.UserRun, "Enabled", Tool)
            .WithRun(StartupSource.UserRun, "NeverTouched", Tool)
            .WithApproval(StartupSource.UserRun, "ByTaskManager", 3, 133_900_000_000_000_000)
            .WithApproval(StartupSource.UserRun, "BySettings", 1, 133_900_000_000_000_000)
            .WithApproval(StartupSource.UserRun, "Enabled", 2);
        _programs.With(Tool);

        var states = Reader().Read(CancellationToken.None).Entries.ToDictionary(e => e.Key.Name, e => e.State);

        Assert.Equal(StartupState.Disabled, states["ByTaskManager"]);
        Assert.Equal(StartupState.Disabled, states["BySettings"]);
        Assert.Equal(StartupState.Starts, states["Enabled"]);
        Assert.Equal(StartupState.Starts, states["NeverTouched"]);
    }

    /// <summary>
    /// The 32-bit view's entries are recorded under <c>Run32</c>, so a value of the same name under the
    /// 64-bit view's <c>Run</c> says nothing about them.
    /// </summary>
    [Fact]
    public void EachRunKeyIsJudgedByItsOwnApprovalKey()
    {
        _registry
            .WithRun(StartupSource.MachineRun, "Tool", Tool)
            .WithRun(StartupSource.MachineRun32, "Tool", Tool)
            .WithApproval(StartupSource.MachineRun, "Tool", 3);
        _programs.With(Tool);

        var entries = Reader().Read(CancellationToken.None).Entries;

        Assert.Equal(StartupState.Disabled, entries.Single(e => e.Key.Source is StartupSource.MachineRun).State);
        Assert.Equal(StartupState.Starts, entries.Single(e => e.Key.Source is StartupSource.MachineRun32).State);
    }

    [Fact]
    public void AnEntryWhoseProgramIsMissingSaysSo()
    {
        _registry.WithRun(StartupSource.UserRun, "Gone", @"C:\Program Files\Gone\gone.exe");
        UserShortcut("Gone.lnk", new ShortcutTarget(@"C:\Program Files\Gone\gone.exe", string.Empty));

        var entries = Reader().Read(CancellationToken.None).Entries;

        Assert.All(entries, e => Assert.Equal(ProgramStanding.Missing, e.Standing));
        Assert.All(entries, e => Assert.NotNull(StartupMarks.For(e, null, Memory.ProcessFigures.Checked, isElevated: false).Missing));
    }

    /// <summary>Windows would not answer, which is not the same as "not there".</summary>
    [Fact]
    public void AProgramWindowsWouldNotDescribeIsNotReportedMissing()
    {
        _registry.WithRun(StartupSource.UserRun, "Held", Tool);
        _programs.With(Tool, presence: PathPresence.Refused);

        var entry = Assert.Single(Reader().Read(CancellationToken.None).Entries);

        Assert.Equal(ProgramStanding.Unknown, entry.Standing);
        Assert.Null(StartupMarks.For(entry, null, Memory.ProcessFigures.Checked, isElevated: false).Missing);
    }

    [Fact]
    public void TheFoldersOwnSettingsAndAnApprovalNamingNoEntryAreNotListed()
    {
        File.WriteAllText(Path.Combine(StartupFolder(_user.StartupFolder!), "desktop.ini"), "[.ShellClassInfo]");
        _registry.WithApproval(StartupSource.UserRun, "Uninstalled", 3).WithApproval(StartupSource.UserStartupFolder, "Old.lnk", 3);

        Assert.Empty(Reader().Read(CancellationToken.None).Entries);
    }

    /// <summary>A key nobody could open is not a key with nothing in it, which would read as "starts".</summary>
    [Fact]
    public void AnApprovalKeyWindowsWouldNotOpenLeavesItsEntriesUnknownAndSaysSo()
    {
        _registry.WithRun(StartupSource.MachineRun, "Tool", Tool).RefusingApprovals(StartupSource.MachineRun);
        _programs.With(Tool);

        var reading = Reader().Read(CancellationToken.None);

        Assert.Equal(StartupState.Unknown, Assert.Single(reading.Entries).State);
        Assert.Contains(reading.Notes, n => n.Contains(StartupSource.MachineRun.ApprovalLocation(), StringComparison.Ordinal));
    }

    [Fact]
    public void ARunKeyWindowsWouldNotOpenIsSaidToBeUnreadRatherThanEmpty()
    {
        _registry.RefusingRun(StartupSource.MachineRun);

        var reading = Reader().Read(CancellationToken.None);

        Assert.Empty(reading.Entries);
        Assert.Contains(reading.Notes, n => n.Contains(StartupSource.MachineRun.Location(null), StringComparison.Ordinal));
    }

    /// <summary>Windows runs the expansion of a <c>REG_EXPAND_SZ</c>, so its program is the expanded path.</summary>
    [Fact]
    public void AnExpandableCommandNamesTheProgramWindowsRuns()
    {
        var windows = Environment.GetEnvironmentVariable("SystemRoot")!;
        var program = Path.Combine(windows, "System32", "tool.exe");
        _registry.WithRun(StartupSource.UserRun, "Tool", @"%SystemRoot%\System32\tool.exe /start", RegistryValueKind.ExpandString);
        _programs.With(program);

        var entry = Assert.Single(Reader().Read(CancellationToken.None).Entries);

        Assert.Equal(program, entry.Program, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(@"%SystemRoot%\System32\tool.exe /start", entry.RunValue!.Text);
    }

    [Fact]
    public void TheTitleIsTheProgramsOwnDescriptionAndThePublisherItsOwnCompany()
    {
        _registry.WithRun(StartupSource.UserRun, "ToolTray", Tool);
        _programs.With(Tool, description: "Tool Tray", company: "Contoso");

        var entry = Assert.Single(Reader().Read(CancellationToken.None).Entries);

        Assert.Equal("Tool Tray", entry.Title);
        Assert.Equal("Contoso", entry.Publisher);
    }

    [Fact]
    public void APackageListWindowsCutShortIsSaidToBeShort()
    {
        var reading = Reader(PackagedTasksRead.Unreadable).Read(CancellationToken.None);

        Assert.Single(reading.Notes);
    }

    private StartupReader Reader(PackagedTasksRead? packaged = null) => new(
        _registry,
        new StartupFolderReader(_user, _system),
        _shortcuts,
        _programs,
        packaged is null ? FakePackagedStartupTasks.None : new FakePackagedStartupTasks(packaged));

    private void UserShortcut(string name, ShortcutTarget target) =>
        _shortcuts.With(WriteItem(_user.StartupFolder!, name), target);

    private void CommonShortcut(string name, ShortcutTarget target) =>
        _shortcuts.With(WriteItem(_system.CommonStartupFolder!, name), target);

    private static string WriteItem(string folder, string name)
    {
        var path = Path.Combine(StartupFolder(folder), name);
        File.WriteAllBytes(path, [1, 2, 3]);

        return path;
    }

    private static string StartupFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        return folder;
    }
}
