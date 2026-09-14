using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.2.1: what Memory shows and what Memory would act on are different sets. These prove where the
/// two part — that a process and its own share are the one program, and that every other node the
/// picture draws, the compression store included, is not a program to ask anything of.
///
/// <para>The compression store is the case worth having: the tree hangs it off the process that holds
/// it, so <see cref="MemoryTree.ProcessOf"/> answers for it, and a page that took that answer for a
/// program would offer to close a part of Windows.</para>
///
/// <para>Every name and figure here is invented.</para>
/// </summary>
public sealed class MemoryTargetTests
{
    [Fact]
    public void AProcessIsTheProgramItDraws()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 500, created: 10)
            .Build());

        var target = MemoryTarget.Of(tree, Node(tree, MemoryPart.Process, 100, 10));

        Assert.NotNull(target);
        Assert.Equal(100, target.ProcessId);
        Assert.Equal("alpha.exe", target.Name);
    }

    /// <summary>
    /// A process with children draws its own pages as a child of its own, and that share is the same
    /// program: picking it is picking the program it belongs to.
    /// </summary>
    [Fact]
    public void AProcessesOwnShareIsThatSameProgram()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 300, created: 10)
            .Process(200, 100, "beta.exe", 100, created: 20)
            .Build());

        var target = MemoryTarget.Of(tree, Node(tree, MemoryPart.OwnShare, 100, 10));

        Assert.NotNull(target);
        Assert.Equal(100, target.ProcessId);
    }

    /// <summary>
    /// The store is drawn under Windows and carries the process that holds it, so the tree answers
    /// with a process for it. It is memory Windows holds rather than a program anybody picked, and
    /// §2 keeps Memory off every memory list there is.
    /// </summary>
    [Fact]
    public void TheCompressionStoreIsNotAProgram()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(500, MemoryTreeBuilder.SystemProcessId, MemoryTreeBuilder.CompressionStoreName, 400, created: 10)
            .Build());

        var node = Node(tree, MemoryPart.CompressionStore);

        // The premise: without it this test would pass against a rule that simply had nothing to say.
        Assert.NotNull(tree.ProcessOf(node));

        Assert.Null(MemoryTarget.Of(tree, node));
    }

    [Theory]
    [InlineData(MemoryPart.PhysicalMemory)]
    [InlineData(MemoryPart.Applications)]
    [InlineData(MemoryPart.Services)]
    [InlineData(MemoryPart.Windows)]
    [InlineData(MemoryPart.Standby)]
    [InlineData(MemoryPart.Unattributed)]
    public void APartOfThePictureIsNotAProgram(MemoryPart part)
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 500, created: 10)
            .Lists(zeroedMiB: 100, freeMiB: 100, modifiedMiB: 100, standbyMiB: 2_000)
            .Build());

        Assert.Null(MemoryTarget.Of(tree, Node(tree, part)));
    }

    /// <summary>
    /// A node number belongs to the reading that gave it, and every reading renumbers them. A caller
    /// holding one from the reading before is asking about something that has gone, which is an
    /// answer rather than a crash.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void ANodeThatIsNotInThisReadingIsNotAProgram(int node)
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 500, created: 10)
            .Build());

        Assert.Null(MemoryTarget.Of(tree, node));
    }

    private static int Node(MemoryTree tree, MemoryPart part) =>
        tree.Find(MemoryNodeKey.Of(part)) ?? throw new InvalidOperationException($"The tree has no {part}.");

    private static int Node(MemoryTree tree, MemoryPart part, int processId, long created) =>
        tree.Find(new MemoryNodeKey(part, processId, created))
            ?? throw new InvalidOperationException($"The tree has no {part} for process {processId}.");
}
