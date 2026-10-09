using Deguffer.Core.Safety;
using Deguffer.Testing;

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
    /// The volume the scratch folder is on names its file system, which is what decides whether a
    /// checksum is kept between searches there: read wrongly as unnamed, no volume would ever keep
    /// one. The suite runs on NTFS, as the rest of its scratch-folder tests need.
    /// </summary>
    [Fact]
    public void NamesTheFileSystemOfTheScratchVolume()
    {
        using var temp = new TempDirectory();

        var volume = VolumeInventory.Describe([Path.GetPathRoot(temp.Path)!]);

        Assert.Equal("NTFS", volume.FileSystem);
        Assert.True(volume.KeepsFileNumbers);
    }

    /// <summary>
    /// A mount point Windows would not describe is told apart from an empty drive. Asked of a folder
    /// standing in for a mount point, because no test may mount a volume, and the readiness is decided
    /// from the mount point's own attributes whatever is mounted there.
    /// </summary>
    [Fact]
    public void TellsARefusedMountPointApartFromAnEmptyDrive()
    {
        using var temp = new TempDirectory();
        var mountPoint = temp.CreateDirectory("mount") + Path.DirectorySeparatorChar;
        var absent = Path.Combine(temp.Path, "no-media") + Path.DirectorySeparatorChar;

        Assert.Equal(VolumeReadiness.Ready, VolumeInventory.Describe([mountPoint]).Readiness);
        Assert.Equal(VolumeReadiness.NoMedia, VolumeInventory.Describe([absent]).Readiness);

        using (DeniedDirectory.WithUnreadableAttributes(mountPoint))
        {
            var refused = VolumeInventory.Describe([mountPoint]);

            Assert.Equal(VolumeReadiness.Refused, refused.Readiness);
            Assert.False(refused.IsReady);
        }
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
    /// Asked live at any of a volume's mount points, with or without its trailing separator, the
    /// answer is every place the remembered list says the volume is mounted, in the same order. A
    /// folder nothing is mounted at answers nothing, and so asks nothing more of a refusing caller.
    /// Makes no claim that any volume here has more than one mount point, for the reason
    /// <see cref="ReportsEveryPathEachVolumeIsMountedAt"/> gives.
    /// </summary>
    [Fact]
    public void AnswersEveryMountPointOfTheVolumeMountedAtOne()
    {
        var volumes = VolumeInventory.Current.Volumes.Where(v => v.VolumeName is not null).ToList();

        Assert.NotEmpty(volumes);

        Assert.All(volumes, v => Assert.All(v.MountPoints, mountPoint =>
        {
            Assert.Equal(v.MountPoints, VolumeInventory.Current.MountPointsOf(mountPoint));
            Assert.Equal(v.MountPoints, VolumeInventory.Current.MountPointsOf(mountPoint[..^1]));
        }));

        using var temp = new TempDirectory();

        Assert.Empty(VolumeInventory.Current.MountPointsOf(temp.Path));
    }

    /// <summary>
    /// What a drive letter stands for, read from the target Windows keeps for it. <c>subst</c> writes
    /// a folder in the <c>\??\</c> namespace, and a volume's own letter names a device, which is no
    /// folder. Asked of the parsing rather than the machine, because no test may create a letter.
    /// </summary>
    [Theory]
    [InlineData(@"\??\C:\Users\testuser", @"C:\Users\testuser")]
    [InlineData(@"\??\C:\Users\testuser\", @"C:\Users\testuser")]
    [InlineData(@"\??\C:", @"C:\")]
    [InlineData(@"\??\C:\", @"C:\")]
    [InlineData(@"\??\UNC\server\share\folder", @"\\server\share\folder")]
    [InlineData(@"\Device\HarddiskVolume3", null)]
    [InlineData(@"\Device\LanmanRedirector\;Z:0000000000012345\server\share", null)]
    [InlineData(@"\??\", null)]
    [InlineData(null, null)]
    public void ASubstitutedLetterStandsForTheFolderItsTargetNames(string? target, string? folder) =>
        Assert.Equal(folder, VolumeCalls.Substitution(target));

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
        Assert.Contains(fixedVolumes, v => v.FeaturesAnswered && v.Features.HasFlag(VolumeFeatures.ReparsePoints));
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

    /// <summary>
    /// §7 shows free space against capacity, so the two figures have to describe the same volume and
    /// degrade together.
    /// </summary>
    [Fact]
    public void ReportsCapacityForTheVolumeHoldingThePath()
    {
        using var temp = new TempDirectory();

        var (total, free) = VolumeInventory.Current.SpaceOf(temp.Path)!.Value;

        // The pairing is what the capacity bar draws: free above capacity would render a negative
        // used-fraction, and a zero capacity would divide by zero.
        Assert.True(total > 0);

        // Strictly greater, so that returning free space in both slots fails here. Any volume able
        // to hold this test's temp directory has something on it, so the two figures are never
        // equal in practice — and if they were, the bar would read empty on a full disk.
        Assert.True(total > free);
    }

    /// <summary>
    /// An unavailable volume is a dash in the UI, not an exception. The drive letter below is
    /// deliberately one Windows reserves for floppies and effectively never mounts.
    /// </summary>
    [Fact]
    public void AnswersNoSpaceForAVolumeThatIsNotThere() =>
        Assert.Null(VolumeInventory.Current.SpaceOf(@"B:\nonexistent\cache"));

    [Fact]
    public void AnswersNoSpaceForAPathThatCannotBeRooted() =>
        Assert.Null(VolumeInventory.Current.SpaceOf(string.Empty));

    /// <summary>
    /// The figure the native route reads is the figure Windows reports for the same volume.
    ///
    /// <para><c>DriveInfo</c> is the independent witness here rather than the implementation: it is
    /// what the space used to be read with before the mount point was resolved natively, and it is
    /// not on the route under test. A rewrite that read the wrong field of <c>GetDiskFreeSpaceEx</c>
    /// — the raw free space rather than the caller's quota, or the total in place of the free — or
    /// that resolved a different volume, disagrees here.</para>
    ///
    /// <para><b>What this cannot show is the defect the rewrite fixed.</b> Telling the two routes
    /// apart needs a volume mounted at a folder, and a test cannot mount one. On a machine whose
    /// volumes all wear drive letters the two agree by construction, which is exactly what is
    /// asserted.</para>
    /// </summary>
    [Fact]
    public void ReportsTheSameSpaceTheVolumeItselfReports()
    {
        using var temp = new TempDirectory();

        var drive = new DriveInfo(Path.GetPathRoot(temp.Path)!);
        var (total, free) = VolumeInventory.Current.SpaceOf(temp.Path)!.Value;

        Assert.Equal(drive.TotalSize, total);

        // Free space moves between two reads on a working machine, so this is a bound rather than
        // an equality: the quota figure can never exceed the volume's capacity, and reading the
        // total into the free slot would break it on any disk holding anything.
        Assert.InRange(free, 0, drive.TotalSize - 1);
    }

    /// <summary>
    /// §6.3: a path in extended-length form measures the same volume as the same path without the
    /// prefix. Every path in Core may arrive as <c>\\?\C:\…</c>, and the mount-point lookup hands
    /// the path to Win32.
    /// </summary>
    [Fact]
    public void ReadsSpaceThroughTheExtendedLengthPrefix()
    {
        using var temp = new TempDirectory();

        Assert.Equal(
            VolumeInventory.Current.SpaceOf(temp.Path)?.Total,
            VolumeInventory.Current.SpaceOf(LongPath.Extended(temp.Path))?.Total);
    }
}
