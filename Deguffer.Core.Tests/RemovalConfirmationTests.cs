using Deguffer.Core.Duplicates;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.4's confirmation: it lists every copy that goes with the counts and space, says what removing
/// a copy in a cloud folder does, and names a drive whose Recycle Bin cannot take what goes to it.
/// </summary>
public sealed class RemovalConfirmationTests : IDisposable
{
    private const string BinKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{11111111-2222-3333-4444-555555555555}";

    private readonly DuplicateTree _tree = new();
    private readonly FakeCloudFiles _cloud = new();
    private readonly LocalVolume _volume;
    private int _files;

    public RemovalConfirmationTests() =>
        _volume = new LocalVolume(
            _tree.Top + Path.DirectorySeparatorChar,
            DriveType.Fixed,
            VolumeReadiness.Ready,
            VolumeName: @"\\?\Volume{11111111-2222-3333-4444-555555555555}\");

    public void Dispose() => _tree.Dispose();

    private string Documents => Path.Combine(_tree.Environment.UserProfile, "Documents");

    private DuplicateCandidate Copy(string folder, string name, long length = 1_000_000) =>
        new(new FileIdentity(1, (UInt128)(++_files)), Path.Combine(folder, name), name, [Path.Combine(folder, name)], 1, length, 4096, DateTime.UnixEpoch, FileStorage.Plain, LocationRole.Search)
        {
            Volume = _volume,
        };

    private DuplicateMarks Marks(params DuplicateCandidate[][] groups)
    {
        var marks = DuplicateMarks.For(
            new CandidateFinding(
                [.. groups.Select(files => new CandidateGroup(files[0].Length, Name: null, Modified: null, files))], [], [], [], [], [], [], [], [], default),
            _tree.Policy(),
            [],
            _tree.Environment,
            _cloud,
            _tree.Volumes,
            _ => new VolumeMedia(StorageMedia.Nvme, [0]),
            _ => new VolumeMedia(StorageMedia.Nvme, [0]),
            FileInformation.Default);

        foreach (var files in groups)
        {
            marks.Add(new DuplicateGroup(MatchCriteria.Content, files[0].Length, Checksum: null, files));
        }

        marks.Complete();

        return marks;
    }

    /// <summary>Marks every copy in each group but its first.</summary>
    private static void MarkAllButFirst(DuplicateMarks marks)
    {
        foreach (var group in marks.Groups)
        {
            foreach (var copy in group.Group.Files.Skip(1))
            {
                Assert.Null(group.Mark(copy, marks.Keeping));
            }
        }
    }

    private static RecycleBinRoom Room(long held, long limit) => new(held, limit, KeepsNothing: false);

    [Fact]
    public void TheConfirmationListsEveryCopyThatGoesWithTheCountsAndSpace()
    {
        var a = Path.Combine(Documents, "A");
        var b = Path.Combine(Documents, "B");
        var c = Path.Combine(Documents, "C");
        var marks = Marks([Copy(a, "1.jpg"), Copy(b, "1.jpg"), Copy(c, "1.jpg")], [Copy(a, "2.jpg"), Copy(b, "2.jpg")]);
        MarkAllButFirst(marks);

        var confirmation = RemovalConfirmation.For(marks, marks.Keeping, ExploreRemovalMode.RecycleBin, _ => Room(0, long.MaxValue), _ => null);

        Assert.Equal(
            marks.Groups.SelectMany(group => group.Group.Files.Skip(1)).Select(copy => copy.Path).Order(),
            confirmation.Copies.Select(copy => copy.Path).Order());
        Assert.Equal(2, confirmation.Groups);
        Assert.Equal(3 * 4096, confirmation.Space);
        Assert.StartsWith("3 copies from 2 groups will be moved to the Recycle Bin", confirmation.Summary);
        Assert.Empty(confirmation.Warnings);
    }

    /// <summary>
    /// A copy moved to the Recycle Bin frees nothing until the bin is emptied, so the summary says so
    /// for a removal to the bin, and not for a permanent one, which frees the space when it is done.
    /// </summary>
    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin, true)]
    [InlineData(ExploreRemovalMode.Permanent, false)]
    public void TheSummarySaysTheRecycleBinFreesNothingUntilItIsEmptiedOnlyForTheBin(ExploreRemovalMode mode, bool says)
    {
        var marks = Marks([Copy(Documents, "1.jpg"), Copy(Path.Combine(Documents, "B"), "1.jpg")]);
        MarkAllButFirst(marks);

        var summary = RemovalConfirmation.For(marks, marks.Keeping, mode, _ => Room(0, long.MaxValue), _ => null).Summary;

        Assert.Contains("the removal may free less", summary, StringComparison.Ordinal);
        Assert.Equal(says, summary.Contains("only when it is emptied", StringComparison.Ordinal));
    }

    [Fact]
    public void TheConfirmationNamesTheBinsRoomWhereTheCopiesExceedIt()
    {
        var marks = Marks([Copy(Documents, "1.jpg"), Copy(Path.Combine(Documents, "B"), "1.jpg"), Copy(Path.Combine(Documents, "C"), "1.jpg")]);
        MarkAllButFirst(marks);

        // Two copies of 1,000,000 bytes go; a bin with 1,500,000 bytes of room left cannot take both.
        var tight = RemovalConfirmation.For(marks, marks.Keeping, ExploreRemovalMode.RecycleBin, _ => Room(held: 500_000, limit: 2_000_000), _ => null);
        var roomy = RemovalConfirmation.For(marks, marks.Keeping, ExploreRemovalMode.RecycleBin, _ => Room(held: 0, limit: 2_000_000), _ => null);

        Assert.Contains(tight.Warnings, warning => warning.Contains("more than it has room for", StringComparison.Ordinal));
        Assert.Empty(roomy.Warnings);
    }

    /// <summary>
    /// A copy the Recycle Bin cannot take, as on a removable drive with no bin, does not go, so it is
    /// listed apart with the bin's reason and counted nowhere among what goes, where before the
    /// confirmation promised the bin for it. A permanent removal takes it like any other, and does not
    /// ask the bin.
    /// </summary>
    [Fact]
    public void ACopyTheBinCannotTakeIsListedAsStayingWithWhyAndNotAsGoing()
    {
        var b = Path.Combine(Documents, "B");
        var usb = Path.Combine(Documents, "USB");
        var marks = Marks([Copy(Documents, "1.jpg"), Copy(b, "1.jpg"), Copy(usb, "1.jpg")], [Copy(Documents, "2.jpg"), Copy(usb, "2.jpg")]);
        MarkAllButFirst(marks);
        List<DuplicateCandidate> asked = [];
        string? NoBinOnTheStick(DuplicateCandidate copy)
        {
            asked.Add(copy);
            return copy.Path.StartsWith(usb, StringComparison.Ordinal) ? "This drive has no Recycle Bin." : null;
        }

        var toBin = RemovalConfirmation.For(marks, marks.Keeping, ExploreRemovalMode.RecycleBin, _ => Room(0, long.MaxValue), NoBinOnTheStick);

        Assert.Equal([Path.Combine(b, "1.jpg")], toBin.Copies.Select(copy => copy.Path));
        Assert.Equal(1, toBin.Groups);
        Assert.Equal(4096, toBin.Space);
        Assert.Equal("Move 1 copy to the Recycle Bin?", toBin.Title);
        Assert.Equal([Path.Combine(usb, "1.jpg"), Path.Combine(usb, "2.jpg")], toBin.Staying.Select(copy => copy.Copy.Path).Order());
        Assert.All(toBin.Staying, copy => Assert.Equal("This drive has no Recycle Bin.", copy.Why));
        Assert.Contains(toBin.Warnings, warning => warning.StartsWith("2 marked copies stay", StringComparison.Ordinal));

        asked.Clear();
        var permanent = RemovalConfirmation.For(marks, marks.Keeping, ExploreRemovalMode.Permanent, _ => null, NoBinOnTheStick);

        Assert.Equal(3, permanent.Copies.Count);
        Assert.Empty(permanent.Staying);
        Assert.Empty(asked);
    }

    /// <summary>
    /// Copies the bin can each take, but not all together, still say that Windows makes room by
    /// deleting the bin's oldest items; the copies that stay are not counted against its room.
    /// </summary>
    [Fact]
    public void ACopyThatStaysIsNotCountedAgainstTheBinsRoom()
    {
        var usb = Path.Combine(Documents, "USB");
        var marks = Marks([Copy(Documents, "1.jpg"), Copy(Path.Combine(Documents, "B"), "1.jpg"), Copy(usb, "1.jpg")]);
        MarkAllButFirst(marks);

        // Room for one copy of 1,000,000 bytes, not two.
        var confirmation = RemovalConfirmation.For(
            marks, marks.Keeping, ExploreRemovalMode.RecycleBin, _ => Room(held: 0, limit: 1_500_000),
            copy => copy.Path.StartsWith(usb, StringComparison.Ordinal) ? "No bin." : null);

        Assert.DoesNotContain(confirmation.Warnings, warning => warning.Contains("more than it has room for", StringComparison.Ordinal));
    }

    [Fact]
    public void TheConfirmationSaysOtherDevicesLoseACopyInACloudFolder()
    {
        var oneDrive = Path.Combine(_tree.Environment.UserProfile, "OneDrive");
        _cloud.Root("OneDrive!S-1!Personal", oneDrive, "OneDrive - Personal");
        var marks = Marks([Copy(Documents, "1.jpg"), Copy(oneDrive, "1.jpg")]);
        MarkAllButFirst(marks);

        var warnings = RemovalConfirmation.For(marks, marks.Keeping, ExploreRemovalMode.Permanent, _ => null, _ => null).Warnings;

        Assert.Contains(warnings, warning => warning.Contains("other devices that sync it lose it too", StringComparison.Ordinal)
            && warning.Contains("OneDrive - Personal", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("removed permanently", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBinsRoomIsReadFromTheVolumesOwnSettings()
    {
        var environment = new FakeUserEnvironment(_tree.Top)
            .WithRegistryNumber(BinKey, "MaxCapacity", 2)
            .WithRegistryNumber(BinKey, "NukeOnDelete", 0);
        List<string> asked = [];
        var rooms = new RecycleBinRooms(root => { asked.Add(root); return 1_000_000; }, environment);

        var room = rooms.Of(_volume);

        Assert.Equal(new RecycleBinRoom(1_000_000, 2 * 1024 * 1024, KeepsNothing: false), room);
        Assert.Equal(2 * 1024 * 1024 - 1_000_000, room!.Free);

        // Asked by the volume's own name, which a folder it is mounted at is not.
        Assert.Equal([_volume.VolumeName!], asked);
    }

    [Fact]
    public void ABinSetToKeepNothingHasNoRoom()
    {
        var environment = new FakeUserEnvironment(_tree.Top)
            .WithRegistryNumber(BinKey, "MaxCapacity", 2048)
            .WithRegistryNumber(BinKey, "NukeOnDelete", 1);

        var room = new RecycleBinRooms(_ => 0, environment).Of(_volume);

        Assert.True(room!.KeepsNothing);
        Assert.Equal(0, room.Free);
    }

    [Fact]
    public void ABinWhoseSettingsAreMissingHasNoKnownRoomAndABinWindowsWillNotDescribeHasNone()
    {
        var environment = new FakeUserEnvironment(_tree.Top);

        Assert.Null(new RecycleBinRooms(_ => 0, environment).Of(_volume)!.Free);
        Assert.Null(new RecycleBinRooms(_ => null, environment).Of(_volume));
    }
}
