using Deguffer.Core.Providers;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="SourceSearchLanes"/>: which approved folders a source discovery may search at the same
/// time. Apart where their disks are known to be apart, and together on a disk known to be solid state.
/// </summary>
public sealed class SourceSearchLanesTests
{
    private static VolumeMedia Solid(params int[] disks) => new(StorageMedia.Nvme, disks);

    private static VolumeMedia Spinning(params int[] disks) => new(StorageMedia.Rotational, disks);

    private static Func<string, VolumeMedia?> Storage(params (string Prefix, VolumeMedia? Media)[] answers) =>
        path => answers
            .Where(answer => path.StartsWith(answer.Prefix, StringComparison.OrdinalIgnoreCase))
            .MaxBy(answer => answer.Prefix.Length)
            .Media;

    [Fact]
    public void FoldersOnASolidStateDiskAreSearchedTogether()
    {
        var lanes = SourceSearchLanes.Of([@"C:\src", @"C:\work", @"C:\games"], Storage((@"C:\", Solid(0))));

        Assert.Equal([[0], [1], [2]], lanes);
    }

    /// <summary>Two walks under one head would make it seek between two trees for the length of both.</summary>
    [Fact]
    public void FoldersOnASpinningDiskAreSearchedInTurnInTheOrderAsked()
    {
        var lanes = SourceSearchLanes.Of(
            [@"E:\a", @"D:\b", @"E:\c"],
            Storage((@"D:\", Spinning(1)), (@"E:\", Spinning(1))));

        Assert.Equal([[0, 1, 2]], lanes);
    }

    [Fact]
    public void FoldersOnSeparateSpinningDisksAreSearchedTogether()
    {
        var lanes = SourceSearchLanes.Of(
            [@"D:\a", @"E:\b", @"D:\c"],
            Storage((@"D:\", Spinning(1)), (@"E:\", Spinning(2))));

        Assert.Equal([[0, 2], [1]], lanes);
    }

    /// <summary>A tuner built without the machine's devices knows no disk, so every folder is searched in turn, as before.</summary>
    [Fact]
    public void WithNothingKnownEveryFolderIsSearchedInTurn()
    {
        var lanes = SourceSearchLanes.Of([@"C:\a", @"D:\b"], _ => null);

        Assert.Equal([[0, 1]], lanes);
    }

    /// <summary>A solid-state volume sharing a disk set with a spinning one would put two walks on the spinning disk.</summary>
    [Fact]
    public void ASpinningDiskInASetKeepsTheWholeSetInTurn()
    {
        var lanes = SourceSearchLanes.Of(
            [@"C:\a", @"D:\b"],
            Storage((@"C:\", Solid(0, 1)), (@"D:\", Spinning(1))));

        Assert.Equal([[0, 1]], lanes);
    }

    /// <summary>
    /// A folder that is a mount point of a solid-state volume, on a spinning volume, has its walk on
    /// the first and its index read on the second.
    /// </summary>
    [Fact]
    public void FoldersMountedFromSolidStateOnASpinningVolumeAreSearchedInTurn()
    {
        var lanes = SourceSearchLanes.Of(
            [@"D:\mounted", @"D:\other"],
            Storage((@"D:\", Spinning(1)), (@"D:\mounted", Solid(1)), (@"D:\other", Solid(1))));

        Assert.Equal([[0, 1]], lanes);
    }

    /// <summary>A share is on no disk of this machine, and two shares can be one server's disks.</summary>
    [Fact]
    public void SharesAreSearchedInTurn()
    {
        var lanes = SourceSearchLanes.Of(
            [@"\\server\a", @"\\server\b"],
            Storage((@"\\", new VolumeMedia(StorageMedia.Network, []))));

        Assert.Equal([[0, 1]], lanes);
    }
}
