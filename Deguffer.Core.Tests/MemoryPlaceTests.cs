using Deguffer.Core.Memory;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A refresh renumbers every node, so what the user is looking at is found again by what it is: a
/// process by its identifier and creation time, never by name or position, and a part of Windows by
/// its part (§7.2).
/// </summary>
public sealed class MemoryPlaceTests
{
    /// <summary>
    /// A new, larger process listed first in the second read, so the one being looked at is numbered
    /// differently there, which the test asserts before relying on it.
    /// </summary>
    [Fact]
    public void AProcessIsFoundAgainByIdentifierAndCreationTime()
    {
        var first = MemoryTreeBuilder.Build(new MemorySnapshotBuilder().Process(100, 1, "alpha.exe", 300, created: 10).Build());
        var second = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(50, 1, "beta.exe", 900, created: 5)
            .Process(100, 1, "alpha.exe", 350, created: 10)
            .Build());

        var standing = first.Find(new MemoryNodeKey(MemoryPart.Process, 100, 10))!.Value;
        var expected = second.Find(new MemoryNodeKey(MemoryPart.Process, 100, 10))!.Value;

        Assert.NotEqual(standing, expected);
        Assert.Equal(expected, MemoryPlace.TryCarry(first, standing, second));
    }

    [Fact]
    public void AnIdentifierReusedByALaterProcessIsNotTheSameProcess()
    {
        var first = MemoryTreeBuilder.Build(new MemorySnapshotBuilder().Process(100, 1, "alpha.exe", 300, created: 10).Build());
        var second = MemoryTreeBuilder.Build(new MemorySnapshotBuilder().Process(100, 1, "alpha.exe", 300, created: 99).Build());

        var standing = first.Find(new MemoryNodeKey(MemoryPart.Process, 100, 10))!.Value;

        Assert.Null(MemoryPlace.TryCarry(first, standing, second));
        Assert.Equal(second.RootNode, MemoryPlace.Carry(first, standing, second));
    }

    [Fact]
    public void APartOfWindowsIsFoundAgain()
    {
        var first = MemoryTreeBuilder.Build(new MemorySnapshotBuilder().Build());
        var second = MemoryTreeBuilder.Build(new MemorySnapshotBuilder().Process(100, 1, "alpha.exe", 300, created: 10).Build());

        var standing = first.Find(MemoryNodeKey.Of(MemoryPart.SystemCache))!.Value;

        Assert.Equal(second.Find(MemoryNodeKey.Of(MemoryPart.SystemCache)), MemoryPlace.TryCarry(first, standing, second));
    }

    [Fact]
    public void AnOwnShareLandsOnItsProcessOnceItsChildrenHaveGone()
    {
        var first = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 300, created: 10)
            .Process(200, 100, "beta.exe", 100, created: 20)
            .Build());
        var second = MemoryTreeBuilder.Build(new MemorySnapshotBuilder().Process(100, 1, "alpha.exe", 300, created: 10).Build());

        var standing = first.Find(new MemoryNodeKey(MemoryPart.OwnShare, 100, 10))!.Value;

        Assert.Equal(
            second.Find(new MemoryNodeKey(MemoryPart.Process, 100, 10)),
            MemoryPlace.TryCarry(first, standing, second));
    }

    [Fact]
    public void NothingOnScreenCarriesNothing()
    {
        var arriving = MemoryTreeBuilder.Build(new MemorySnapshotBuilder().Build());

        Assert.Null(MemoryPlace.TryCarry(null, 3, arriving));
        Assert.Equal(arriving.RootNode, MemoryPlace.Carry(null, 3, arriving));
    }
}
