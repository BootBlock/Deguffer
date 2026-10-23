using Deguffer.Core.Safety;
using Deguffer.Core.Startup;
using Deguffer.Testing;
using Microsoft.Win32;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the real registry reader and writer do with values the registry itself stores, against
/// scratch keys standing in for a <c>Run</c> key and its <c>StartupApproved</c> key. A fake would be
/// asserting its own reply: these are the types the platform hands back.
/// </summary>
public sealed class WindowsStartupRegistryTests : IDisposable
{
    private readonly ScratchKey _scratch = new();
    private readonly RegistryKey _run;
    private readonly WindowsStartupRegistry _registry;

    public WindowsStartupRegistryTests()
    {
        _run = _scratch.Key.CreateSubKey("Run");
        _registry = new WindowsStartupRegistry(
            _ => Registry.CurrentUser.OpenSubKey($@"{_scratch.Path}\Run"),
            (_, writable) => writable
                ? Registry.CurrentUser.CreateSubKey($@"{_scratch.Path}\StartupApproved\Run", writable: true)
                : Registry.CurrentUser.OpenSubKey($@"{_scratch.Path}\StartupApproved\Run"));
    }

    public void Dispose()
    {
        _run.Dispose();
        _scratch.Dispose();
    }

    /// <summary>
    /// Read as stored, so the check after a change compares the bytes Windows keeps. An expanded
    /// command would compare equal to a different value that expands the same way.
    /// </summary>
    [Fact]
    public void AnExpandableCommandIsReadAsStored()
    {
        _run.SetValue("Tool", @"%SystemRoot%\tool.exe", RegistryValueKind.ExpandString);

        var value = _registry.ReadRun(StartupSource.UserRun).Find("Tool")!;

        Assert.Equal(RegistryValueKind.ExpandString, value.Kind);
        Assert.Equal(@"%SystemRoot%\tool.exe", value.Text);
    }

    [Fact]
    public void AnApprovalKeyNotThereYetIsAbsentAndAFirstWriteCreatesIt()
    {
        Assert.Equal(PathPresence.Absent, _registry.ReadApprovals(StartupSource.UserRun).Presence);

        _registry.WriteApproval(StartupSource.UserRun, "Tool", Approval.Starting());

        var read = _registry.ReadApprovals(StartupSource.UserRun);
        Assert.Equal(PathPresence.Present, read.Presence);
        Assert.True(read.Find("Tool")!.SameAs(Approval.Starting()));
    }

    [Fact]
    public void WritingAnApprovalLeavesTheRunKeyAsItWas()
    {
        _run.SetValue("Tool", @"C:\Program Files\Tool\tool.exe");
        var before = _registry.ReadRun(StartupSource.UserRun);

        _registry.WriteApproval(StartupSource.UserRun, "Tool", Approval.DisabledOn(DateTimeOffset.UnixEpoch));

        var after = _registry.ReadRun(StartupSource.UserRun);
        Assert.Equal(before.Values.Keys, after.Values.Keys);
        Assert.True(after.Find("Tool")!.SameAs(before.Find("Tool")));
    }

    [Fact]
    public void ValuesNameCaseDoesNotMatter()
    {
        _registry.WriteApproval(StartupSource.UserRun, "Tool", Approval.Starting());

        Assert.NotNull(_registry.ReadApprovals(StartupSource.UserRun).Find("TOOL"));
    }
}
