using Deguffer.Core.Execution;
using Deguffer.Core.Startup;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.5's one change: one <c>StartupApproved</c> value written, the entry itself never, and the three
/// exact checks of §5.6 after it. Every refusal writes nothing at all.
/// </summary>
public sealed class StartupSwitcherTests : IDisposable
{
    private const string Tool = @"C:\Program Files\Tool\tool.exe";

    private readonly TempDirectory _root = new();
    private readonly ManualTimeProvider _time = new();
    private readonly FakeUserEnvironment _user;
    private readonly FakeSystemDirectories _system;
    private readonly FakeStartupRegistry _registry = new();
    private readonly FakeProgramFiles _programs = new FakeProgramFiles().With(Tool);

    public StartupSwitcherTests()
    {
        _user = new FakeUserEnvironment(_root.CreateDirectory("user"));
        _system = new FakeSystemDirectories(_root.CreateDirectory("system"));
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void DisablingWritesOnlyTheApprovalValueAndLeavesTheEntryAndItsNeighboursAlone()
    {
        _registry
            .WithRun(StartupSource.UserRun, "Tool", Tool)
            .WithRun(StartupSource.UserRun, "Other", Tool)
            .WithApproval(StartupSource.UserRun, "Other", 3, 133_000_000_000_000_000)
            .WithApproval(StartupSource.UserRun, "Removed long ago", 2);

        var report = Switcher().Switch(Entry("Tool"), isElevated: false);

        var write = Assert.Single(_registry.Writes);
        Assert.Equal((StartupSource.UserRun, "Tool"), (write.Source, write.Name));
        Assert.Equal([3, 0, 0, 0, .. BitConverter.GetBytes(_time.GetUtcNow().UtcDateTime.ToFileTimeUtc())], write.Value.Bytes);

        Assert.True(report.Changed);
        Assert.True(report.IsComplete);
        Assert.Equal(StartupChange.Disable, report.Change);

        // §5.6, all three: what was written, the entry, and every value beside it.
        Assert.Contains(report.Verification.Checks, c => c.Outcome is VerificationOutcome.Sent);
        Assert.Contains(report.Verification.Checks, c => c.Reason == "The entry itself" && c.Outcome is VerificationOutcome.Survived);
        Assert.Equal(2, report.Verification.Checks.Count(c => c.Reason.StartsWith("A value beside", StringComparison.Ordinal) && c.Outcome is VerificationOutcome.Survived));
        Assert.Equal(Tool, _registry.ReadRun(StartupSource.UserRun).Find("Tool")!.Text);
    }

    /// <summary>Re-enabling restores the value Task Manager writes, whichever tool disabled the entry.</summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    public void EnablingWritesTaskManagersEnabledValue(uint disabledBy)
    {
        _registry.WithRun(StartupSource.UserRun, "Tool", Tool).WithApproval(StartupSource.UserRun, "Tool", disabledBy, 133_000_000_000_000_000);

        var report = Switcher().Switch(Entry("Tool"), isElevated: false);

        Assert.True(report.IsComplete);
        Assert.Equal(StartupChange.Enable, report.Change);
        Assert.Equal(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, Assert.Single(_registry.Writes).Value.Bytes);
    }

    [Fact]
    public void AStartupFolderItemIsSwitchedByItsFileNameAndTheFileIsLeftAsItWas()
    {
        var folder = Directory.CreateDirectory(_user.StartupFolder!).FullName;
        var item = Path.Combine(folder, "Tool.lnk");
        File.WriteAllBytes(item, [1, 2, 3]);
        var written = File.GetLastWriteTimeUtc(item);

        var report = Switcher().Switch(Entry("Tool.lnk", StartupSource.UserStartupFolder), isElevated: false);

        Assert.True(report.IsComplete);
        Assert.Equal((StartupSource.UserStartupFolder, "Tool.lnk"), (_registry.Writes[0].Source, _registry.Writes[0].Name));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(item));
        Assert.Equal(written, File.GetLastWriteTimeUtc(item));
    }

    /// <summary>
    /// The check after the write is exact, so it has to fail when something beside the value changed.
    /// Here something else rewrote a neighbouring value while the change ran.
    /// </summary>
    [Fact]
    public void ANeighbourThatChangedDuringTheChangeFailsTheCheck()
    {
        _registry
            .WithRun(StartupSource.UserRun, "Tool", Tool)
            .WithApproval(StartupSource.UserRun, "Other", 2);
        _registry.AfterWrite = registry => registry.WithApproval(StartupSource.UserRun, "Other", 3);

        var report = Switcher().Switch(Entry("Tool"), isElevated: false);

        Assert.True(report.Changed);
        Assert.False(report.Verification.Passed);
        var failure = Assert.Single(report.Verification.Failures);
        Assert.EndsWith(@"\Other", failure.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryThatChangedDuringTheChangeFailsTheCheck()
    {
        _registry.WithRun(StartupSource.UserRun, "Tool", Tool);
        _registry.AfterWrite = registry => registry.WithRun(StartupSource.UserRun, "Tool", @"C:\elsewhere\tool.exe");

        var report = Switcher().Switch(Entry("Tool"), isElevated: false);

        Assert.Equal("The entry itself", Assert.Single(report.Verification.Failures).Reason);
    }

    [Fact]
    public void AValueInAFormNobodyExplainedIsNeverReplaced()
    {
        _registry.WithRun(StartupSource.UserRun, "Tool", Tool).WithApproval(StartupSource.UserRun, "Tool", 6);

        var report = Switcher().Switch(Entry("Tool"), isElevated: false);

        Assert.False(report.Changed);
        Assert.Null(report.Change);
        Assert.Empty(_registry.Writes);
    }

    [Theory]
    [InlineData(StartupSource.MachineRun)]
    [InlineData(StartupSource.MachineRun32)]
    public void AMachineWideEntryIsRefusedUnelevatedBeforeAnythingIsWritten(StartupSource source)
    {
        _registry.WithRun(source, "Tool", Tool);

        var report = Switcher().Switch(Entry("Tool", source), isElevated: false);

        Assert.False(report.Changed);
        Assert.Contains("administrator", report.Message, StringComparison.Ordinal);
        Assert.Empty(_registry.Writes);
    }

    [Fact]
    public void AMachineWideEntryIsChangedElevated()
    {
        _registry.WithRun(StartupSource.MachineRun32, "Tool", Tool);

        var report = Switcher().Switch(Entry("Tool", StartupSource.MachineRun32), isElevated: true);

        Assert.True(report.IsComplete);
        Assert.Equal(StartupSource.MachineRun32, Assert.Single(_registry.Writes).Source);
    }

    [Fact]
    public void APackagedTaskIsReportOnly()
    {
        var task = new StartupEntry(
            new StartupKey(StartupSource.PackagedTask, "AppStartup", "Pkg_1234567890abc"),
            "Packaged app", null, null, ProgramStanding.Unknown, null, StartupState.Starts, Approval: null);

        var report = Switcher().Switch(task, isElevated: true);

        Assert.False(report.Changed);
        Assert.Contains("Settings", report.Message, StringComparison.Ordinal);
        Assert.Empty(_registry.Writes);
    }

    [Fact]
    public void AnEntryThatChangedSinceItWasReadIsLeft()
    {
        _registry.WithRun(StartupSource.UserRun, "Tool", Tool);
        var chosen = Entry("Tool");
        _registry.WithRun(StartupSource.UserRun, "Tool", @"C:\elsewhere\tool.exe");

        var report = Switcher().Switch(chosen, isElevated: false);

        Assert.False(report.Changed);
        Assert.Empty(_registry.Writes);
    }

    [Fact]
    public void AnEntryThatWentSinceItWasReadIsLeft()
    {
        var folder = Directory.CreateDirectory(_user.StartupFolder!).FullName;
        File.WriteAllBytes(Path.Combine(folder, "Tool.lnk"), [1]);
        var chosen = Entry("Tool.lnk", StartupSource.UserStartupFolder);
        File.Delete(Path.Combine(folder, "Tool.lnk"));

        var report = Switcher().Switch(chosen, isElevated: false);

        Assert.Contains("gone", report.Message, StringComparison.Ordinal);
        Assert.Empty(_registry.Writes);
    }

    [Fact]
    public void AStateThatChangedSinceItWasReadIsLeft()
    {
        _registry.WithRun(StartupSource.UserRun, "Tool", Tool);
        var chosen = Entry("Tool");
        _registry.WithApproval(StartupSource.UserRun, "Tool", 1, 133_000_000_000_000_000);

        var report = Switcher().Switch(chosen, isElevated: false);

        Assert.False(report.Changed);
        Assert.Empty(_registry.Writes);
    }

    [Fact]
    public void AWriteWindowsRefusedChangesNothingAndStillChecksTheEntry()
    {
        _registry.WithRun(StartupSource.UserRun, "Tool", Tool).RefusingWrites(StartupSource.UserRun);

        var report = Switcher().Switch(Entry("Tool"), isElevated: false);

        Assert.False(report.Changed);
        Assert.Contains("refused", report.Message, StringComparison.Ordinal);
        Assert.Contains(report.Verification.Checks, c => c.Reason == "The entry itself" && c.Outcome is VerificationOutcome.Survived);
        Assert.DoesNotContain(report.Verification.Checks, c => c.Outcome is VerificationOutcome.Sent);
    }

    private StartupSwitcher Switcher() =>
        new(_registry, new StartupFolderReader(_user, _system), _time);

    /// <summary>The entry as the page would have read it a moment ago.</summary>
    private StartupEntry Entry(string name, StartupSource source = StartupSource.UserRun) =>
        new StartupReader(_registry, new StartupFolderReader(_user, _system), new FakeShortcutReader(), _programs, FakePackagedStartupTasks.None)
            .Read(CancellationToken.None)
            .Find(new StartupKey(source, name))
        ?? throw new InvalidOperationException($"The test wrote no entry {name}.");
}
