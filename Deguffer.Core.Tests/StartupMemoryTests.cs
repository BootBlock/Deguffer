using Deguffer.Core.Memory;
using Deguffer.Core.Startup;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What an entry's program holds now (§7.5): matched by full path, opening only the processes whose
/// image name could be that program, and never a figure where §7.2 turned the figures off.
/// </summary>
public sealed class StartupMemoryTests
{
    private const string Tool = @"C:\Program Files\Tool\tool.exe";
    private const string OtherTool = @"C:\Portable\tool.exe";

    [Fact]
    public void EveryProcessRunningTheProgramIsCountedAndOnlyThose()
    {
        var calls = new FakeProcessCalls()
            .With(new FakeProcess { ProcessId = 10, Image = Tool })
            .With(new FakeProcess { ProcessId = 11, Image = Tool })
            .With(new FakeProcess { ProcessId = 12, Image = OtherTool })
            .With(new FakeProcess { ProcessId = 13, Image = @"C:\Windows\explorer.exe" });
        var table = Table(
            Process(10, "tool.exe", 100), Process(11, "tool.exe", 50), Process(12, "tool.exe", 7), Process(13, "explorer.exe", 900));

        var memory = StartupMemory.Match(Reading(Tool), table, new ProcessImages(calls));

        Assert.Equal(new RunningProgram(2, 150), memory.For(Key));

        // The process table names the image alone, so a process of another name is never opened.
        Assert.Equal([10, 11, 12], calls.Opened.Order());
    }

    [Fact]
    public void AProgramWithNoProcessIsNotRunning()
    {
        var memory = StartupMemory.Match(Reading(Tool), Table(), new ProcessImages(new FakeProcessCalls()));

        Assert.Equal(RunningProgram.NotRunning, memory.For(Key));
    }

    /// <summary>
    /// Where §7.2's check failed, a process cannot be told from a successor holding its identifier, so
    /// nothing is opened and nothing is matched, rather than a figure that reads as "holds nothing".
    /// </summary>
    [Fact]
    public void FiguresTurnedOffMatchNothingAndOpenNothing()
    {
        var calls = new FakeProcessCalls().With(new FakeProcess { ProcessId = 10, Image = Tool });
        var table = new ProcessMemoryTable([Process(10, "tool.exe", null) with { CreationTime = null }], ProcessFigures.PrivateWorkingSetDisagrees, Complete: true);

        var memory = StartupMemory.Match(Reading(Tool), table, new ProcessImages(calls));

        Assert.Null(memory.For(Key));
        Assert.Empty(calls.Opened);
        Assert.Null(StartupMarks.For(Reading(Tool).Entries[0], memory.For(Key), memory.Figures, isElevated: false).IsRunning);
    }

    /// <summary>The identifier now belongs to a later process, so its program says nothing about the one the table named.</summary>
    [Fact]
    public void AProcessWhoseIdentifierPassedToAnotherIsNotCounted()
    {
        var calls = new FakeProcessCalls().With(new FakeProcess { ProcessId = 10, Image = Tool, CreatedAt = FakeProcess.Created + 1 });

        var memory = StartupMemory.Match(Reading(Tool), Table(Process(10, "tool.exe", 100)), new ProcessImages(calls));

        Assert.Equal(RunningProgram.NotRunning, memory.For(Key));
    }

    [Fact]
    public void AProcessThatHasExitedIsNotCounted()
    {
        var calls = new FakeProcessCalls().With(new FakeProcess { ProcessId = 10, Image = Tool, Exited = true });

        var memory = StartupMemory.Match(Reading(Tool), Table(Process(10, "tool.exe", 100)), new ProcessImages(calls));

        Assert.Equal(RunningProgram.NotRunning, memory.For(Key));
    }

    /// <summary>Windows compares paths without case, and gives an image path in whatever case the file has.</summary>
    [Fact]
    public void ThePathIsComparedAsWindowsComparesIt()
    {
        var calls = new FakeProcessCalls().With(new FakeProcess { ProcessId = 10, Image = Tool.ToUpperInvariant() });

        var memory = StartupMemory.Match(Reading(Tool), Table(Process(10, "TOOL.EXE", 64)), new ProcessImages(calls));

        Assert.Equal(new RunningProgram(1, 64), memory.For(Key));
    }

    private static readonly StartupKey Key = new(StartupSource.UserRun, "Tool");

    private static StartupReading Reading(string program) => new(
        [new StartupEntry(Key, "Tool", program, program, ProgramStanding.Present, null, StartupState.Starts, Approval.None)],
        []);

    private static ProcessMemoryTable Table(params ProcessMemory[] processes) => new(processes, ProcessFigures.Checked, Complete: true);

    private static ProcessMemory Process(int id, string name, long? privateWorkingSet) =>
        new(id, 4, name, FakeProcessCalls.OwnSession, privateWorkingSet, privateWorkingSet, FakeProcess.Created);
}
