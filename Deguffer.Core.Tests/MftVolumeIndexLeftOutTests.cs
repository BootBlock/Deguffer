using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The table's half of a measurement that leaves entries out: each entry placed as one record, and
/// that record neither counted nor entered. <see cref="ScanBoundsTests"/> holds the walks to the same
/// figures.
/// </summary>
public sealed class MftVolumeIndexLeftOutTests
{
    // An invented profile's scratch folder.
    private const uint Users = 6;
    private const uint Profile = 7;
    private const uint Temp = 8;
    private const uint Session = 9;
    private const uint Adobe = 10;
    private const uint DiskCache = 11;

    private const uint Abandoned = 20;
    private const uint Working = 21;
    private const uint Beside = 22;
    private const uint Frame = 23;
    private const uint Installer = 24;

    private static readonly string[] TempPath = ["Users", "testuser", "temp"];

    private static MftFixture Tree() => new MftFixture()
        .AddDirectory(Users, MftRecord.RootRecordNumber, "Users")
        .AddDirectory(Profile, Users, "testuser")
        .AddDirectory(Temp, Profile, "temp")
        .AddDirectory(Session, Temp, "live-session")
        .AddDirectory(Adobe, Temp, "Adobe")
        .AddDirectory(DiskCache, Adobe, "Disk Cache")
        .AddFile(Abandoned, Temp, "abandoned.tmp", allocated: 1024, logical: 1000)
        .AddFile(Working, Session, "working.txt", allocated: 8192, logical: 5000)
        .AddFile(Beside, Adobe, "beside.tmp", allocated: 4096, logical: 3000)
        .AddFile(Frame, DiskCache, "frame.bin", allocated: 12288, logical: 11000)
        .AddFile(Installer, Temp, "installer.msi", allocated: 16384, logical: 13000);

    private static MftVolumeIndex Build(MftFixture fixture)
    {
        using var source = fixture.Build();

        Assert.True(MftVolumeIndexBuilder.TryBuild(source, TableTuning.Default, out var index));
        return index;
    }

    /// <summary>A file Windows describes with <paramref name="record"/> as its number.</summary>
    private static FileReading Numbered(ulong record, ushort sequence = 3) => new(
        FileReadingResult.Identified,
        new FileDescription(
            new FileIdentity(Volume: 1, File: ((ulong)sequence << 48) | record),
            Path: @"C:\Users\testuser\temp\entry",
            Length: 0,
            Allocated: 0,
            Names: 1,
            Modified: DateTime.UnixEpoch,
            Changed: DateTime.UnixEpoch,
            Attributes: FileAttributes.Archive,
            ReparseTag: 0));

    /// <summary>A describer that answers by the entry's name, and fails the test for any it was not given.</summary>
    private static Func<IReadOnlyList<string>, FileReading> Describing(IReadOnlyDictionary<string, FileReading> byName) =>
        components => byName.TryGetValue(components[^1], out var reading)
            ? reading
            : throw new InvalidOperationException($"{string.Join('\\', components)} was described, and only a file the table cannot name should be.");

    private static ScanSize? Measure(MftVolumeIndex index, IReadOnlySet<uint> leftOut, bool keepRoot = true) =>
        index.TryMeasure(TempPath, MinimumAge.Off, keepRoot, leftOut, out _, out _);

    /// <summary>
    /// The figure the subtraction gave: the folder whole, less each entry left out measured on its
    /// own. The installer is a file, which the table cannot measure alone, so its size is the
    /// fixture's.
    /// </summary>
    [Fact]
    public void TheFigureEqualsTheTreeLessEachEntryMeasuredOnItsOwn()
    {
        var index = Build(Tree());

        var placed = index.TryPlace(
            TempPath,
            [["live-session"], ["Adobe", "Disk Cache"], ["installer.msi"]],
            Describing(new Dictionary<string, FileReading> { ["installer.msi"] = Numbered(Installer) }));

        Assert.Equal(new HashSet<uint> { Session, DiskCache, Installer }, placed);

        var whole = index.TryMeasure(TempPath)!.Value;
        var subtracted = whole.Logical
            - index.TryMeasure([.. TempPath, "live-session"])!.Value.Logical
            - index.TryMeasure([.. TempPath, "Adobe", "Disk Cache"])!.Value.Logical
            - 13000;

        var bounded = Measure(index, placed!)!.Value;

        Assert.Equal(1000 + 3000, bounded.Logical);
        Assert.Equal(subtracted, bounded.Logical);
        Assert.Equal(1024 + 4096, bounded.Allocated);

        // abandoned.tmp and beside.tmp. The folder is emptied in place, and Adobe holds what stays.
        Assert.Equal(2, bounded.Entries);
    }

    /// <summary>
    /// A record left out is not entered. Below it is a file whose size the table could not establish,
    /// which makes any subtree that reaches it unmeasurable, so a figure at all proves the walk of
    /// the table stopped at the entry.
    /// </summary>
    [Fact]
    public void NeverEntersARecordLeftOut()
    {
        var index = Build(Tree().AddFileDescribingOnlyALaterExtent(30, Session, "split.bin"));

        Assert.Null(index.TryMeasure(TempPath));
        Assert.Equal(1000 + 3000 + 11000 + 13000, Measure(index, new HashSet<uint> { Session })!.Value.Logical);
    }

    /// <summary>
    /// The folder emptied in place is not among what its clean takes, whether or not anything inside
    /// it stays. Without that the planner took one off a count that had not included it.
    /// </summary>
    [Fact]
    public void KeepingTheRootLeavesOnlyItsOwnEntryOut()
    {
        var index = Build(Tree());
        var none = new HashSet<uint>();

        // Five files and four folders, and the root among them where it is not kept.
        Assert.Equal(9, Measure(index, none, keepRoot: false)!.Value.Entries);
        Assert.Equal(8, Measure(index, none)!.Value.Entries);
    }

    /// <summary>
    /// A number is believed only where the table puts that record in the folder the path names. A table
    /// read before the file was written may hold another file at that number, and leaving that one out
    /// would make the figure short by a file nobody named.
    /// </summary>
    [Fact]
    public void RefusesAFileWhoseNumberTheTablePutsElsewhere()
    {
        var index = Build(Tree());

        Assert.Null(index.TryPlace(
            TempPath,
            [["installer.msi"]],
            Describing(new Dictionary<string, FileReading> { ["installer.msi"] = Numbered(Frame) })));

        Assert.Null(index.TryPlace(
            TempPath,
            [["installer.msi"]],
            Describing(new Dictionary<string, FileReading> { ["installer.msi"] = Numbered(1_000_000) })));
    }

    /// <summary>
    /// A file Windows would not describe may still be there, so the table cannot say which record to
    /// leave out and the caller walks instead. One Windows says is gone is in no total to leave out of.
    /// </summary>
    [Fact]
    public void RefusesAnUnreadableFileAndPassesOverAGoneOne()
    {
        var index = Build(Tree());

        Assert.Null(index.TryPlace(
            TempPath,
            [["installer.msi"]],
            Describing(new Dictionary<string, FileReading> { ["installer.msi"] = FileReading.Unreadable })));

        Assert.Empty(index.TryPlace(
            TempPath,
            [["installer.msi"]],
            Describing(new Dictionary<string, FileReading> { ["installer.msi"] = FileReading.Gone }))!);
    }

    /// <summary>
    /// An entry whose folder the table does not hold is outside every subtree the table totals, so it
    /// is passed over without asking the disk, rather than refused.
    /// </summary>
    [Fact]
    public void PassesOverAnEntryWhoseFolderTheTableDoesNotHold()
    {
        var index = Build(Tree());

        Assert.Empty(index.TryPlace(TempPath, [["written-since", "profile.tmp"]], Describing(new Dictionary<string, FileReading>()))!);
    }

    /// <summary>
    /// A folder may hold two children whose names differ only in case, as WSL's trees do. The one
    /// named exactly is the one left out, as a path given in that case resolves to it, and a name
    /// given in neither case finds the first in record order.
    /// </summary>
    [Fact]
    public void PlacesANameAsAPathInThatCaseResolves()
    {
        var index = Build(Tree()
            .AddDirectory(30, Temp, "Profile")
            .AddDirectory(31, Temp, "profile"));
        var describe = Describing(new Dictionary<string, FileReading>());

        Assert.Equal(new HashSet<uint> { 31 }, index.TryPlace(TempPath, [["profile"]], describe));
        Assert.Equal(new HashSet<uint> { 30 }, index.TryPlace(TempPath, [["Profile"]], describe));
        Assert.Equal(new HashSet<uint> { 30 }, index.TryPlace(TempPath, [["PROFILE"]], describe));
    }
}
