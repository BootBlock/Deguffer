using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The space Windows states the size of and no folder holds: restore points and shadow copies, and
/// reserved storage. Each is taken out of the unaccounted block at Windows' own figure and drawn
/// under its own name, or not at all, and never as a thing a click can pick (§7.1).
/// </summary>
public sealed class HiddenSpaceTests
{
    private const int Width = 800;
    private const int Height = 600;

    /// <summary>12,000 bytes with 6,000 free, beside a scan of 3,000, leaves 3,000 unaccounted.</summary>
    private static readonly VolumeSpace Volume = new(12_000, 6_000);

    [Fact]
    public void AShadowStorageReadingOfNIsANamedBlockOfNAndTheUnaccountedBlockShrinksByExactlyN()
    {
        var tiles = Layout(Volume with { Hidden = Shadow(allocated: 1_000) });

        var shadow = tiles.Single(tile => tile.IsShadowCopies);

        Assert.Equal(1_000, shadow.Bytes);
        Assert.Equal(2_000, tiles.Single(tile => tile.IsUnaccounted).Bytes);
        Assert.False(shadow.IsNode);
    }

    [Fact]
    public void AReservedStorageReadingOfNIsANamedBlockOfNAndTheUnaccountedBlockShrinksByExactlyN()
    {
        var tiles = Layout(Volume with { Hidden = Reserved(500) });

        var reserved = tiles.Single(tile => tile.IsReservedStorage);

        Assert.Equal(500, reserved.Bytes);
        Assert.Equal(2_500, tiles.Single(tile => tile.IsUnaccounted).Bytes);
        Assert.False(reserved.IsNode);
    }

    [Fact]
    public void BothReadingsComeOutOfTheSameUnaccountedBlock()
    {
        var hidden = new HiddenSpace(Shadow(allocated: 1_000).ShadowCopies, Reserved(500).Reserved);

        Assert.Equal(new VolumeParts(1_000, 500, 1_500, 6_000), (Volume with { Hidden = hidden }).Parts(3_000));
    }

    /// <summary>
    /// The allocated figure is drawn, not the used one: it is what the volume's free space is short
    /// by. The other two are for the note.
    /// </summary>
    [Fact]
    public void TheShadowStorageIsDrawnAtWhatWindowsTookFromTheVolume()
    {
        var hidden = new HiddenSpace(new ShadowStorage(Statement.Stated, 400, 1_000, 2_000), default);

        Assert.Equal(1_000, (Volume with { Hidden = hidden }).Parts(3_000).ShadowCopies);
    }

    [Fact]
    public void ARefusedReadingDrawsNoNamedBlockAndLeavesTheUnaccountedBlockAsItIs()
    {
        var tiles = Layout(Volume with { Hidden = new HiddenSpace(ShadowStorage.Refused, default) });

        Assert.DoesNotContain(tiles, tile => tile.IsShadowCopies || tile.IsReservedStorage);
        Assert.Equal(3_000, tiles.Single(tile => tile.IsUnaccounted).Bytes);
    }

    /// <summary>
    /// A part larger than what the scan left over has been counted, at least partly, by the scan. Cut
    /// down, it would show a size Windows never stated, and subtracted whole it would make the
    /// unaccounted block negative. It is not drawn, and the block keeps its bytes.
    /// </summary>
    [Fact]
    public void AReadingLargerThanTheUnaccountedSpaceNeverMakesAnotherBlockNegative()
    {
        var tiles = Layout(Volume with { Hidden = Shadow(allocated: 5_000) });

        Assert.DoesNotContain(tiles, tile => tile.IsShadowCopies);
        Assert.Equal(3_000, tiles.Single(tile => tile.IsUnaccounted).Bytes);
        Assert.All(tiles, tile => Assert.True(tile.Bytes >= 0));
    }

    /// <summary>
    /// The reserve is taken first, because no scan can have counted it. What is left after it decides
    /// whether the shadow copy storage fits.
    /// </summary>
    [Fact]
    public void ThePartThatNoLongerFitsAfterTheReserveIsLeftInTheUnaccountedBlock()
    {
        var hidden = new HiddenSpace(Shadow(allocated: 1_000).ShadowCopies, Reserved(2_500).Reserved);

        Assert.Equal(new VolumeParts(0, 2_500, 500, 6_000), (Volume with { Hidden = hidden }).Parts(3_000));
    }

    /// <summary>
    /// An elevated scan of the file table counts System Volume Information, where the shadow copy
    /// storage is kept. Drawing Windows' figure beside it as well would draw the same bytes twice.
    /// </summary>
    [Fact]
    public void ShadowStorageTheScanCountedIsNotDrawnASecondTime()
    {
        var tiles = Layout(Volume with { Hidden = Shadow(allocated: 1_000), CountedSystemVolumeInformation = true });

        Assert.DoesNotContain(tiles, tile => tile.IsShadowCopies);
        Assert.Equal(3_000, tiles.Single(tile => tile.IsUnaccounted).Bytes);
    }

    [Fact]
    public async Task TheFiguresAreReadOnceForTheScannedVolumeInThePlainFormOfItsRoot()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", totalBytes: 12_000, freeBytes: 6_000);
        var hidden = new FakeHiddenSpaceSource { Answer = Shadow(allocated: 1_000) };

        var volume = await VolumeSpace.ReadAsync(volumes, hidden, @"\\?\D:\", Tree(@"\\?\D:\", Svi.Absent), default);

        Assert.Equal([@"D:\"], hidden.Asked);
        Assert.Equal(new VolumeSpace(12_000, 6_000, Shadow(allocated: 1_000)), volume);
    }

    [Fact]
    public async Task AScanOfAFolderAsksWindowsNothing()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", totalBytes: 12_000, freeBytes: 6_000);
        var hidden = new FakeHiddenSpaceSource { Answer = Shadow(allocated: 1_000) };

        var volume = await VolumeSpace.ReadAsync(volumes, hidden, @"D:\work", Tree(@"D:\work", Svi.Absent), default);

        Assert.Empty(hidden.Asked);
        Assert.Equal(VolumeSpace.None, volume);
    }

    /// <summary>
    /// A walk is refused System Volume Information even as administrator, and marks it unknown with
    /// nothing counted. A folder the scan read any of, or every size of, has had its storage counted,
    /// and a part read is counted too, so what the scan read there is never drawn a second time.
    /// </summary>
    [Theory]
    [InlineData(Svi.Refused, false)]
    [InlineData(Svi.Counted, true)]
    [InlineData(Svi.PartlyRead, true)]
    public async Task SystemVolumeInformationIsCountedWhereTheScanCountedAnythingInIt(Svi state, bool counted)
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", totalBytes: 12_000, freeBytes: 6_000);

        var volume = await VolumeSpace.ReadAsync(
                volumes, new FakeHiddenSpaceSource(), @"D:\", Tree(@"D:\", state), default);

        Assert.Equal(counted, volume.CountedSystemVolumeInformation);
    }

    [Fact]
    public async Task AVolumeWithoutSystemVolumeInformationInTheTreeHasNotHadItCounted()
    {
        var volumes = new FakeVolumeInventory().With(@"D:\", totalBytes: 12_000, freeBytes: 6_000);

        var volume = await VolumeSpace.ReadAsync(
                volumes, new FakeHiddenSpaceSource(), @"D:\", Tree(@"D:\", Svi.Absent), default);

        Assert.False(volume.CountedSystemVolumeInformation);
    }

    /// <summary>Where the scan counted the storage, the folder holding it says how much of it is the storage.</summary>
    [Fact]
    public void SystemVolumeInformationStatesWindowsFigureWhereTheScanCountedTheStorageInIt()
    {
        var tree = Tree(@"D:\", Svi.Counted);
        var folder = Child(tree, VolumeSpace.SystemVolumeInformation);
        var counted = Volume with
        {
            Hidden = new HiddenSpace(new ShadowStorage(Statement.Stated, 400, 1_000, 2_000), default),
            CountedSystemVolumeInformation = true,
        };

        var note = HiddenSpaceNote.For(tree, folder, counted);

        Assert.Contains(FreeSpace.Format(1_000), note);
        Assert.Contains(FreeSpace.Format(2_000), note);
        Assert.Empty(HiddenSpaceNote.For(tree, Child(tree, "folder"), counted));
        Assert.Empty(HiddenSpaceNote.For(tree, folder, counted with { CountedSystemVolumeInformation = false }));
    }

    [Fact]
    public void TheShadowCopyNoteGivesWindowsFiguresAndItsOwnWayToReduceThem()
    {
        var note = HiddenSpaceNote.ShadowCopies(new ShadowStorage(Statement.Stated, 400, 1_000, MaximumBytes: null));

        Assert.Contains(FreeSpace.Format(400), note);
        Assert.Contains("sets no limit", note);
        Assert.Contains("System Protection", note);
    }

    /// <summary>
    /// A figure Windows refuses an unelevated process is one more thing elevating adds, whatever
    /// route the scan took. A volume that is not NTFS has no file table to offer instead.
    /// </summary>
    [Fact]
    public void ARefusedFigureKeepsTheElevationOffered()
    {
        var refused = new HiddenSpace(ShadowStorage.Refused, default);

        Assert.True(ElevationOffer.ShouldOffer(isElevated: false, FallbackReason.NotNtfsVolume, refused));
        Assert.False(ElevationOffer.ShouldOffer(isElevated: false, FallbackReason.NotNtfsVolume, Shadow(allocated: 1_000)));
        Assert.False(ElevationOffer.ShouldOffer(isElevated: true, FallbackReason.NotNtfsVolume, refused));
    }

    private static IReadOnlyList<ExploreTile> Layout(VolumeSpace volume)
    {
        var tree = Tree(@"C:\", Svi.Absent);

        Assert.Equal(3_000, tree.TotalBytes);

        return TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default, volume);
    }

    private static HiddenSpace Shadow(long allocated) =>
        new(new ShadowStorage(Statement.Stated, allocated, allocated, allocated * 2), default);

    private static HiddenSpace Reserved(long bytes) => new(default, new ReservedStorage(Statement.Stated, bytes));

    /// <summary>
    /// A folder of three 1,000-byte files below <paramref name="root"/>, with System Volume
    /// Information beside it in the given state.
    /// </summary>
    private static ExploreTree Tree(string root, Svi systemVolumeInformation)
    {
        var builder = new ExploreTreeBuilder(root);

        var folder = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [new ExploreChild("folder", IsDirectory: true, IsLink: false, Size: 0)]);

        builder.AddChildren(
            folder,
            [.. Enumerable.Range(0, 3).Select(i =>
                new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 1000))]);

        if (systemVolumeInformation is not Svi.Absent)
        {
            var hidden = builder.AddChildren(
                ExploreTreeBuilder.RootNode,
                [new ExploreChild(VolumeSpace.SystemVolumeInformation, IsDirectory: true, IsLink: false, Size: 0)]);

            if (systemVolumeInformation is Svi.PartlyRead)
            {
                builder.AddChildren(hidden, [new ExploreChild("read", IsDirectory: false, IsLink: false, Size: 100)]);
            }

            if (systemVolumeInformation is Svi.Refused or Svi.PartlyRead)
            {
                builder.MarkSizeUnknown(hidden);
            }
        }

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static int Child(ExploreTree tree, string name)
    {
        foreach (var child in tree.ChildrenOf(tree.RootNode))
        {
            if (tree.NameOf(child) == name)
            {
                return child;
            }
        }

        throw new InvalidOperationException($"No {name} below the root.");
    }

    /// <summary>What a scan found of System Volume Information.</summary>
    public enum Svi
    {
        Absent,
        Counted,
        Refused,
        PartlyRead,
    }
}
