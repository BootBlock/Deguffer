using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// What §7.2.1's one exact §5.6 negative is about: the shell window's owner and this session's
/// compositor, found in the read taken as the close was sent.
///
/// <para>The whole point of this type is what it does when it cannot find them. An assertion nobody
/// could make must not read as one that passed, so every way the desktop goes unnamed is carried out
/// of here rather than dropped.</para>
/// </summary>
public sealed class DesktopProcessesTests
{
    private const int Shell = 100;
    private const int Compositor = 120;
    private const int OtherCompositor = 121;

    private const long ShellCreated = 11;
    private const long CompositorCreated = 12;
    private const long OtherCompositorCreated = 13;

    private static MemorySnapshot Machine() =>
        new MemorySnapshotBuilder()
            .Process(Shell, 1, "explorer.exe", 200, ShellCreated)
            .Process(Compositor, 1, "dwm.exe", 150, CompositorCreated)
            .Process(4321, Shell, "editor.exe", 300, created: 20)
            .Build();

    private static FakeProcessCalls Windows(params FakeProcess[] processes)
    {
        var calls = new FakeProcessCalls();

        foreach (var process in processes)
        {
            calls.With(process);
        }

        return calls;
    }

    private static FakeProcess Living(int processId, long created) =>
        new() { ProcessId = processId, CreatedAt = created };

    private static DesktopSet Of(
        MemorySnapshot machine, ShellOwner shell, FakeProcessCalls processes) =>
        DesktopProcesses.Of(ProcessTree.Of(machine), shell, processes, CancellationToken.None);

    [Fact]
    public void TheShellAndThisSessionsCompositorAreWhatTheCloseMustLeaveStanding()
    {
        var machine = Machine();

        var desktop = Of(
            machine,
            ShellOwner.Is(Shell),
            Windows(Living(Shell, ShellCreated), Living(Compositor, CompositorCreated)));

        Assert.Equal([Shell, Compositor], desktop.Processes.Select(p => p.ProcessId));
        Assert.Empty(desktop.Unestablished);
    }

    /// <summary>
    /// The shell's identifier comes from Windows now and its record comes from a read a moment
    /// earlier. A shell that restarted in between holds the same number and is not the same process,
    /// so §7.2.1's "by identifier and creation time" is the only sound way to bind the two.
    /// </summary>
    [Fact]
    public void AShellThatRestartedSinceTheReadIsNotTakenForTheOneInIt()
    {
        var machine = Machine();

        var desktop = Of(
            machine,
            ShellOwner.Is(Shell),
            Windows(Living(Shell, ShellCreated + 900), Living(Compositor, CompositorCreated)));

        Assert.DoesNotContain(desktop.Processes, p => p.ProcessId == Shell);
        Assert.Contains("shell window", Assert.Single(desktop.Unestablished), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every way the shell goes unnamed is carried, because a close whose one exact negative was
    /// never built has to say so rather than report a clean run.
    /// </summary>
    [Theory]
    [InlineData(false, false)] // Windows would not say whose the shell window is.
    [InlineData(true, false)]  // It named an owner the read taken a moment earlier does not hold.
    [InlineData(true, true)]   // It named one that will not open, so its identity cannot be confirmed.
    public void AShellThatCannotBeNamedIsRecordedRatherThanDropped(bool named, bool inTheRead)
    {
        var machine = Machine();
        var shell = named ? ShellOwner.Is(inTheRead ? Shell : 7777) : ShellOwner.Unreadable;

        var desktop = Of(
            machine,
            shell,
            Windows(
                new FakeProcess { ProcessId = Shell, CreatedAt = ShellCreated, OpenRefused = true },
                Living(Compositor, CompositorCreated)));

        Assert.DoesNotContain(desktop.Processes, p => p.ProcessId == Shell);
        Assert.Contains("shell window", Assert.Single(desktop.Unestablished), StringComparison.Ordinal);
    }

    /// <summary>
    /// Where Windows reports no shell window at all there is nothing to assert and nothing missing,
    /// which is a different answer from one it would not give.
    /// </summary>
    [Fact]
    public void NoShellWindowLeavesNothingToLookForRatherThanSomethingUnestablished()
    {
        var machine = Machine();

        var desktop = Of(machine, ShellOwner.None, Windows(Living(Compositor, CompositorCreated)));

        Assert.Equal([Compositor], desktop.Processes.Select(p => p.ProcessId));
        Assert.Empty(desktop.Unestablished);
    }

    /// <summary>
    /// Another session's compositor is not this desktop, and a close in this session cannot end it
    /// either way. Dropping it keeps a signed-out user's compositor from failing a run in which
    /// nothing went wrong.
    /// </summary>
    [Fact]
    public void ACompositorInAnotherSessionIsNotThisDesktop()
    {
        var machine = new MemorySnapshotBuilder()
            .Process(Shell, 1, "explorer.exe", 200, ShellCreated)
            .Process(Compositor, 1, "dwm.exe", 150, CompositorCreated)
            .Process(OtherCompositor, 1, "dwm.exe", 90, OtherCompositorCreated)
            .Build();

        var desktop = Of(
            machine,
            ShellOwner.Is(Shell),
            Windows(
                Living(Shell, ShellCreated),
                Living(Compositor, CompositorCreated),
                new FakeProcess
                {
                    ProcessId = OtherCompositor,
                    CreatedAt = OtherCompositorCreated,
                    Session = FakeProcessCalls.OtherSession,
                }));

        Assert.Equal([Shell, Compositor], desktop.Processes.Select(p => p.ProcessId));
        Assert.Empty(desktop.Unestablished);
    }

    /// <summary>
    /// §7.2.1: "one whose session will not answer is kept rather than dropped, because a fact nobody
    /// established is not a pass". Keeping it can only add an assertion, and the assertion it adds is
    /// about a process no close can end.
    /// </summary>
    [Theory]
    [InlineData(true)]  // Windows will not open it at all.
    [InlineData(false)] // It opens, and will not say which session it is in.
    public void ACompositorWhoseSessionWillNotAnswerIsKept(bool refusesToOpen)
    {
        var machine = Machine();

        var desktop = Of(
            machine,
            ShellOwner.Is(Shell),
            Windows(
                Living(Shell, ShellCreated),
                new FakeProcess
                {
                    ProcessId = Compositor,
                    CreatedAt = CompositorCreated,
                    OpenRefused = refusesToOpen,
                    Session = null,
                }));

        Assert.Contains(desktop.Processes, p => p.ProcessId == Compositor);
        Assert.Empty(desktop.Unestablished);
    }

    /// <summary>
    /// A read holding no compositor cannot assert the compositor survived, and says so. Every session
    /// with a desktop has one, so this is a read that fell short rather than a machine without one.
    /// </summary>
    [Fact]
    public void AReadWithNoCompositorInItSaysSoRatherThanAssertingNothing()
    {
        var machine = new MemorySnapshotBuilder()
            .Process(Shell, 1, "explorer.exe", 200, ShellCreated)
            .Build();

        var desktop = Of(machine, ShellOwner.Is(Shell), Windows(Living(Shell, ShellCreated)));

        Assert.Equal([Shell], desktop.Processes.Select(p => p.ProcessId));
        Assert.Contains("compositor", Assert.Single(desktop.Unestablished), StringComparison.Ordinal);
    }
}
