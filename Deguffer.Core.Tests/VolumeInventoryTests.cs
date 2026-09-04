using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// The real inventory, which is the one seam a fake cannot stand in for: what is asserted here is
/// that the machine is read at all, and in the shape the providers expect.
///
/// Deliberately makes no claim about <em>which</em> volumes exist. A test that expected <c>C:</c>
/// would pass everywhere and prove nothing anybody cares about, and every rule that actually
/// decides a deletion is asserted through <see cref="Fakes.FakeVolumeInventory"/> instead.
/// </summary>
public sealed class VolumeInventoryTests
{
    [Fact]
    public void ReadsTheMachinesVolumesAsRootedPaths()
    {
        var volumes = VolumeInventory.Current.Volumes;

        Assert.NotEmpty(volumes);
        Assert.All(volumes, v => Assert.True(Path.IsPathRooted(v.RootPath), v.RootPath));
    }

    /// <summary>
    /// The label and the two space figures are what the Explore picker offers a drive by, and they
    /// come from the same reading as the mount point. A fixed volume that is ready always answers
    /// all three, so a null here means the reading was never made.
    /// </summary>
    [Fact]
    public void ReadsTheSpaceEveryReadyFixedVolumeReports()
    {
        var fixedVolumes = VolumeInventory.Current.Volumes
            .Where(v => v.IsReady && v.Kind == DriveType.Fixed)
            .ToList();

        Assert.NotEmpty(fixedVolumes);

        Assert.All(fixedVolumes, v =>
        {
            Assert.NotNull(v.TotalBytes);
            Assert.NotNull(v.FreeBytes);
            Assert.True(v.TotalBytes > 0, v.RootPath);
            Assert.InRange(v.FreeBytes!.Value, 0, v.TotalBytes!.Value);

            // Null or a real name, never the empty string a volume with no label reports.
            Assert.True(v.Label is null or { Length: > 0 }, v.RootPath);
        });
    }

    /// <summary>
    /// The list is remembered for the life of a pass (G4), so the same instance has to come back
    /// until it is dropped — and a drive mounted while the app was open has to be seen after.
    /// </summary>
    [Fact]
    public void RemembersTheListUntilItIsInvalidated()
    {
        var inventory = new VolumeInventory();

        var first = inventory.Volumes;
        Assert.Same(first, inventory.Volumes);

        inventory.Invalidate();

        var second = inventory.Volumes;
        Assert.NotSame(first, second);
        Assert.Equal(first.Select(v => v.RootPath), second.Select(v => v.RootPath));
    }
}
