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
    /// The space figures the Explore picker offers a drive by come from the same reading as the
    /// mount point. A ready fixed volume answers both, so a null here means the reading was never
    /// made.
    ///
    /// <para>This one test does depend on the machine, unlike the rest of the class: it asserts a
    /// ready fixed volume exists so that <see cref="Assert.All{T}"/> cannot pass over an empty
    /// list and prove nothing. That is a claim about a <em>kind</em> of volume, which the machine
    /// running the suite necessarily has, and still no claim about which letter it wears.</para>
    ///
    /// <para><b>The label is not asserted, because nothing here could discriminate it.</b> The
    /// empty-to-null mapping only shows on a volume that has no label, and whether the machine has
    /// one of those is not a property of this code. Asserting the shape of whatever labels happen
    /// to be present would pass identically with the mapping deleted.</para>
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
