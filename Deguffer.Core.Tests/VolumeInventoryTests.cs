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
    /// Every mount point is a rooted path ending in a separator, which is the form
    /// <see cref="HostVolume.For"/> compares against and the form <c>Path.Combine</c> builds a bin
    /// path from. <see cref="LocalVolume.RootPath"/> is the first of them.
    ///
    /// <para>Makes no claim that any volume here has more than one mount point: whether the machine
    /// running the suite has a folder-mounted volume is not a property of this code. What the rule
    /// does for a volume that has several is asserted through
    /// <see cref="Fakes.FakeVolumeInventory"/> instead.</para>
    /// </summary>
    [Fact]
    public void ReportsEveryPathEachVolumeIsMountedAt()
    {
        var volumes = VolumeInventory.Current.Volumes;

        Assert.NotEmpty(volumes);

        Assert.All(volumes, v =>
        {
            var mountPoints = v.MountPoints.ToList();

            Assert.Equal(v.RootPath, mountPoints[0]);

            Assert.All(mountPoints, mountPoint =>
            {
                Assert.True(Path.IsPathRooted(mountPoint), mountPoint);
                Assert.True(Path.EndsInDirectorySeparator(mountPoint), mountPoint);
            });
        });
    }

    /// <summary>
    /// One entry per volume. The list is built from the volume enumeration and then topped up with
    /// the drive letters no volume claimed, so a letter counted twice would put the same volume in
    /// front of the picker twice and plan its Recycle Bin twice.
    ///
    /// <para>This is what the top-up step is held to: every letter on an ordinary machine belongs to
    /// a volume the enumeration already named, so adding them unconditionally fails here.</para>
    /// </summary>
    [Fact]
    public void ReportsEachVolumeOnce()
    {
        var mountPoints = VolumeInventory.Current.Volumes.SelectMany(v => v.MountPoints).ToList();

        Assert.Equal(
            mountPoints.Count,
            mountPoints.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// A volume with a drive letter is rooted at that letter, whatever order Windows named its
    /// mount points in. The root is what a plan targets and what the picker shows, so it is chosen
    /// by a rule rather than taken from the answer.
    ///
    /// <para>Asked of the rule directly. Through <see cref="VolumeInventory.Volumes"/> it could only
    /// be asked of a volume mounted in more than one place, and a test cannot mount one — so on this
    /// machine the rule would never run and every ordering would pass. See the grant in
    /// <c>Deguffer.Core.csproj</c>.</para>
    /// </summary>
    [Theory]
    [InlineData(@"C:\Mount\", @"D:\")]
    [InlineData(@"D:\", @"C:\Mount\")]
    public void RootsAVolumeAtItsDriveLetterWhereItHasOne(string first, string second)
    {
        Assert.Equal(@"D:\", VolumeInventory.Ordered([first, second])[0]);
    }

    /// <summary>
    /// With no letter to prefer, the shortest mount point is the root and the order is settled
    /// between two of the same length. Two reads of one machine must choose the same root, or the
    /// picker's selection and a plan's targets move underneath the reader.
    /// </summary>
    [Fact]
    public void RootsALetterlessVolumeAtTheShortestMountPoint()
    {
        Assert.Equal(
            [@"C:\Mnt\", @"C:\Mount\", @"D:\Mount\"],
            VolumeInventory.Ordered([@"D:\Mount\", @"C:\Mount\", @"C:\Mnt\"]));
    }

    /// <summary>
    /// Every mount point survives the ordering. Dropping one would hand the paths below it to the
    /// volume its mount point sits on, which is the whole defect this rule was written for.
    /// </summary>
    [Fact]
    public void KeepsEveryMountPointItWasGiven()
    {
        string[] mountPoints = [@"D:\Mount\", @"C:\Mount\", @"E:\"];

        Assert.Equal(
            mountPoints.OrderBy(m => m, StringComparer.Ordinal),
            VolumeInventory.Ordered(mountPoints).OrderBy(m => m, StringComparer.Ordinal));
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
    /// The volume flags are actually read, which is the whole of what this seam can prove about
    /// them. Nothing here asserts that any particular drive is or is not cloud storage: that is a
    /// property of the machine, and the rule deciding it is held to measured flag words in
    /// <see cref="LocalVolumeTests"/>.
    ///
    /// <para>What is asserted is that <em>some</em> ready fixed volume reports reparse-point
    /// support, which every Windows machine's NTFS system disk does. With the reading dropped —
    /// every volume left at <see cref="VolumeFeatures.None"/> — this fails, and so would a reading
    /// that came back empty because the call was made wrongly.</para>
    /// </summary>
    [Fact]
    public void ReadsWhatTheVolumesSayTheySupport()
    {
        var fixedVolumes = VolumeInventory.Current.Volumes
            .Where(v => v.IsReady && v.Kind == DriveType.Fixed)
            .ToList();

        Assert.NotEmpty(fixedVolumes);
        Assert.Contains(fixedVolumes, v => v.Features.HasFlag(VolumeFeatures.ReparsePoints));
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
