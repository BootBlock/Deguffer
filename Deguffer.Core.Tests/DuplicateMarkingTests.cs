using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.4's marking: which copies can be kept, which are never marked or refused, the per-group mark
/// state that keeps a copy in every group, and the named rules.
///
/// <para>The groups are built as a search would hand them over, on a scratch drive whose own folders
/// decide what Explore's policy refuses, beside a second drive standing for a USB disk that Windows
/// reports as fixed. What a drive's disks are is read through the media cache from the bus each
/// reports, as on a real machine.</para>
/// </summary>
public sealed class DuplicateMarkingTests : IDisposable
{
    private const string Usb = @"U:\";

    private const string Unknown = @"V:\";

    private static readonly DateTime Older = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = Older.AddDays(1);
    private static readonly DateTime Newest = Older.AddDays(2);

    private readonly DuplicateTree _tree = new();
    private readonly FakeCloudFiles _cloud = new();
    private readonly List<StorageClean> _cleans = [];
    private readonly List<ProgramFolder> _programs = [];
    private readonly List<UnsearchedLocation> _unsearchedReferences = [];
    private readonly LocalVolume _internal;
    private readonly LocalVolume _usb = new(Usb, DriveType.Fixed, VolumeReadiness.Ready);
    private readonly VolumeMediaCache _media;
    private int _files;

    public DuplicateMarkingTests()
    {
        // Where Windows puts it, inside the profile, so no refusal of the folder holding it answers first.
        _tree.Environment.WithTempPath(Path.Combine(_tree.Environment.LocalAppData, "Temp"));
        _tree.Volumes.With(Usb).With(Unknown);
        _internal = new LocalVolume(_tree.Top + Path.DirectorySeparatorChar, DriveType.Fixed, VolumeReadiness.Ready);
        _media = new VolumeMediaCache(new FakeStorageQueries()
            .Volume(_internal.RootPath, 0).Disk(0, bus: 0x11, seekPenalty: null)
            .Volume(Usb, 1).Disk(1, bus: 0x07, seekPenalty: null)
            .Volume(Unknown, 2).Disk(2, bus: 0x0B, seekPenalty: null));
    }

    public void Dispose() => _tree.Dispose();

    private string Documents => Path.Combine(_tree.Environment.UserProfile, "Documents");

    private string Downloads => Path.Combine(_tree.Environment.UserProfile, "Downloads");

    private DuplicateCandidate Copy(
        string path,
        DateTime? modified = null,
        LocationRole role = LocationRole.Search,
        LocalVolume? volume = null,
        FileStorage storage = FileStorage.Plain,
        IReadOnlyList<string>? names = null) =>
        new(
            new FileIdentity(1, (UInt128)(++_files)),
            path,
            Path.GetFileName(path),
            names ?? [path],
            names?.Count ?? 1,
            Length: 100,
            SizeOnDisk: 4096,
            modified ?? Older,
            storage,
            role)
        {
            Volume = volume ?? _internal,
        };

    private DuplicateCandidate OnUsb(string name, DateTime? modified = null, LocationRole role = LocationRole.Search) =>
        Copy(Path.Combine(Usb, "Backup", name), modified, role, _usb);

    private ExploreActionPolicy? _policy;

    private DuplicateMarks Marks(params DuplicateCandidate[][] groups) =>
        DuplicateMarks.For(
            new DuplicateSearchResult(
                new CandidateFinding([], [], _unsearchedReferences, [], [], [], [], _programs, [], default),
                [.. groups.Select(files => new DuplicateGroup(MatchCriteria.Content, 100, Checksum: null, files))],
                default,
                Stopped: false),
            _policy ?? _tree.Policy(),
            _cleans,
            _tree.Environment,
            _cloud,
            _tree.Volumes,
            _media.Of,
            FileInformation.Default);

    private static GroupMarks Only(DuplicateMarks marks) => Assert.Single(marks.Groups);

    private string InTemp(string name) => Path.Combine(_tree.Environment.TempPath, name);

    [Fact]
    public void NothingIsMarkedWhenASearchFinishes()
    {
        var marks = Marks(
            [Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "a.jpg"))],
            [Copy(Path.Combine(Documents, "b.jpg")), Copy(Path.Combine(Downloads, "b.jpg"))]);

        Assert.All(marks.Groups, group => Assert.Empty(group.Standing(marks.Keeping)));
        Assert.All(marks.Groups, group => Assert.All(group.Group.Files, file => Assert.False(group.IsMarked(file))));
    }

    [Fact]
    public void TheLastCopyThatCanBeKeptCannotBeMarkedByHand()
    {
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "a.jpg"))];
        var marks = Marks(files);
        var group = Only(marks);

        Assert.Null(group.Mark(files[0], marks.Keeping));
        Assert.NotNull(group.Mark(files[1], marks.Keeping));
        Assert.False(group.IsMarked(files[1]));
    }

    [Fact]
    public void ACopyThatCannotBeKeptLeavesTheOtherAsTheLastThatCanBe()
    {
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.jpg")), OnUsb("a.jpg")];
        var marks = Marks(files);
        var group = Only(marks);

        // The copy on the USB disk can be marked, and the internal one is then the last that can be kept.
        Assert.Null(group.Mark(files[1], marks.Keeping));
        Assert.NotNull(group.Mark(files[0], marks.Keeping));
    }

    public static TheoryData<string> RuleNames => ["newest", "oldest", "shortest path", "keep in folder", "mark in folder"];

    [Theory]
    [MemberData(nameof(RuleNames))]
    public void NoRuleMarksTheLastCopyThatCanBeKept(string name)
    {
        // The only copy that can be kept is the oldest, with the longest path, outside the folder a
        // rule is told to keep, and inside the folder a rule is told to mark.
        var keptOnly = Copy(Path.Combine(Documents, "Long folder name", "a.jpg"), Older);
        var marks = Marks([keptOnly, OnUsb("a.jpg", Newest), Copy(InTemp("a.jpg"), Newer)]);

        MarkingRule rule = name switch
        {
            "newest" => new MarkingRule.KeepNewest(),
            "oldest" => new MarkingRule.KeepOldest(),
            "shortest path" => new MarkingRule.KeepShortestPath(),
            "keep in folder" => new MarkingRule.KeepInFolder(Downloads),
            _ => new MarkingRule.MarkInFolder(Path.GetDirectoryName(keptOnly.Path)!),
        };

        marks.Run(rule);

        Assert.Null(marks.Keeping.WhyNotKept(keptOnly));
        Assert.False(Only(marks).IsMarked(keptOnly));
    }

    /// <summary>
    /// A rule that keeps the copies in a folder marks nothing in a group with no copy there that can
    /// be kept, though the group could keep another.
    /// </summary>
    [Fact]
    public void KeepInAFolderMarksNothingInAGroupWithNoCopyThere()
    {
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "a.jpg"))];
        var marks = Marks(files);

        var outcome = marks.Run(new MarkingRule.KeepInFolder(Path.Combine(Documents, "Camera")));

        Assert.Equal(0, outcome.Marked);
        Assert.All(files, file => Assert.False(Only(marks).IsMarked(file)));
    }

    /// <summary>
    /// A rule that marks the copies in a folder marks nothing in a group whose every copy that can be
    /// kept is there, rather than all but one of them.
    /// </summary>
    [Fact]
    public void MarkInAFolderMarksNothingWhereEveryCopyThatCanBeKeptIsThere()
    {
        var camera = Path.Combine(Documents, "Camera");
        DuplicateCandidate[] files = [Copy(Path.Combine(camera, "a.jpg")), Copy(Path.Combine(camera, "Old", "a.jpg")), OnUsb("a.jpg")];
        var marks = Marks(files);

        var outcome = marks.Run(new MarkingRule.MarkInFolder(camera));

        Assert.Equal(0, outcome.Marked);
        Assert.All(files, file => Assert.False(Only(marks).IsMarked(file)));
    }

    [Fact]
    public void AStaleMarkStateKeepsNoMarkThatWouldLeaveNothingToKeep()
    {
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "a.jpg"))];
        var marks = Marks(files);
        var group = Only(marks);
        Assert.Null(group.Mark(files[0], marks.Keeping));

        // The unmarked copy's folder becomes the temporary folder after the mark was made.
        _tree.Environment.WithTempPath(Downloads);
        var now = Marks(files).Keeping;

        Assert.True(group.IsMarked(files[0]));
        Assert.Empty(group.Standing(now));
        Assert.Equal(0, group.MarkedSpace(now));
    }

    [Fact]
    public void WhereNoCopyCanBeKeptEveryCopyStaysUnmarkedWithTheReason()
    {
        DuplicateCandidate[] files = [Copy(InTemp("a.jpg")), OnUsb("a.jpg")];
        var marks = Marks(files);
        var group = Only(marks);

        Assert.NotNull(group.WhyNothingCanBeKept(marks.Keeping));
        Assert.All(files, file => Assert.Equal(group.WhyNothingCanBeKept(marks.Keeping), group.Mark(file, marks.Keeping)));
        Assert.All(files, file => Assert.NotNull(marks.Keeping.WhyNotKept(file)));

        var outcome = marks.Run(new MarkingRule.KeepNewest());

        Assert.Equal(0, outcome.Marked);
        Assert.Equal(group.WhyNothingCanBeKept(marks.Keeping), Assert.Single(outcome.Untouched).Reason);
    }

    [Theory]
    [InlineData(LocationRole.Search)]
    [InlineData(LocationRole.Reference)]
    public void ACopyInTheTemporaryFolderNeverCountsAsKept(LocationRole role)
    {
        var marks = Marks([Copy(InTemp("a.jpg"), role: role), Copy(Path.Combine(Documents, "a.jpg"))]);

        Assert.Contains("temporary folder", marks.Keeping.WhyNotKept(Only(marks).Group.Files[0]));
    }

    [Theory]
    [InlineData(LocationRole.Search)]
    [InlineData(LocationRole.Reference)]
    public void ACopyWhereAStorageCleanDeletesNeverCountsAsKept(LocationRole role)
    {
        var cache = Path.Combine(_tree.Environment.LocalAppData, "npm-cache");
        _cleans.Add(new StorageClean(CleanedPlace.Whole(cache), "npm cache"));
        _cleans.Add(new StorageClean(CleanedPlace.FoldersNamed(Path.Combine(Documents, "Source"), ["node_modules"]), "Node.js packages"));

        var marks = Marks(
            [Copy(Path.Combine(cache, "_cacache", "a.tgz"), role: role), Copy(Path.Combine(Documents, "a.tgz"))],
            [Copy(Path.Combine(Documents, "Source", "app", "node_modules", "x", "b.js"), role: role), Copy(Path.Combine(Documents, "b.js"))]);

        Assert.All(marks.Groups, group => Assert.Contains("Storage's", marks.Keeping.WhyNotKept(group.Group.Files[0])));
        Assert.All(marks.Groups, group => Assert.Null(marks.Keeping.WhyNotKept(group.Group.Files[1])));
    }

    /// <summary>
    /// A clean's place named through a junction is the folder the junction leads to, which is the
    /// path a copy found there carries.
    /// </summary>
    [Fact]
    public void APlaceNamedThroughAJunctionHoldsTheCopiesAtTheFolderItLeadsTo()
    {
        var real = _tree.Folder("Caches", "npm-cache");
        var named = Path.Combine(_tree.Environment.LocalAppData, "npm-cache");
        Directory.CreateDirectory(_tree.Environment.LocalAppData);
        Junction.ToDirectory(named, real);
        _cleans.Add(new StorageClean(CleanedPlace.Whole(named), "npm cache"));

        var marks = Marks([Copy(Path.Combine(real, "a.tgz")), Copy(Path.Combine(Documents, "a.tgz"))]);

        Assert.Contains("Storage's", marks.Keeping.WhyNotKept(Only(marks).Group.Files[0]));
    }

    [Fact]
    public void ACopyOnAUsbDiskThatSaysItIsFixedCountsAsKeptOnlyInAReference()
    {
        var marks = Marks([OnUsb("a.jpg"), OnUsb("b.jpg", role: LocationRole.Reference), Copy(Path.Combine(Documents, "a.jpg"))]);
        var files = Only(marks).Group.Files;

        Assert.Equal(DriveType.Fixed, files[0].Volume.Kind);
        Assert.Contains("removable or USB", marks.Keeping.WhyNotKept(files[0]));
        Assert.Null(marks.Keeping.WhyNotKept(files[1]));
    }

    /// <summary>
    /// A disk on a bus that says nothing of its speed, and that would not say whether it incurs a seek
    /// penalty, is of no known kind: it may be a USB disk behind a bridge Windows did not report.
    /// </summary>
    [Fact]
    public void ACopyOnADriveOfNoKnownKindIsNotCountedOnOutsideAReference()
    {
        var unknown = new LocalVolume(Unknown, DriveType.Fixed, VolumeReadiness.Ready);
        var marks = Marks(
        [
            Copy(Path.Combine(Unknown, "a.jpg"), volume: unknown),
            Copy(Path.Combine(Unknown, "b.jpg"), role: LocationRole.Reference, volume: unknown),
            Copy(Path.Combine(Documents, "a.jpg")),
        ]);
        var files = Only(marks).Group.Files;

        Assert.Contains("what kind of drive", marks.Keeping.WhyNotKept(files[0]));
        Assert.Null(marks.Keeping.WhyNotKept(files[1]));
    }

    /// <summary>
    /// Storage's cloud row releases a file's local copy and deletes nothing, so it names no place, and
    /// a reference copy in a cloud folder it works in is still one a group can keep.
    /// </summary>
    [Fact]
    public async Task AReferenceCopyWhoseCloudFileStorageOnlyReleasesStillCounts()
    {
        var oneDrive = Path.Combine(_tree.Environment.UserProfile, "OneDrive");
        _cloud.Root("OneDrive!S-1!Personal", oneDrive, "OneDrive - Personal");
        var releases = new CloudLocalCopiesProvider(_cloud, _tree.Environment);
        _cleans.AddRange((await releases.CleanedPlacesAsync()).Select(place => new StorageClean(place, releases.Name)));

        var marks = Marks([Copy(Path.Combine(oneDrive, "a.jpg"), role: LocationRole.Reference), Copy(Path.Combine(Documents, "a.jpg"))]);

        Assert.Null(marks.Keeping.WhyNotKept(Only(marks).Group.Files[0]));
    }

    [Fact]
    public void ACopyInACloudFolderCountsAsKeptOnlyInAReference()
    {
        var oneDrive = Path.Combine(_tree.Environment.UserProfile, "OneDrive");
        _cloud.Root("OneDrive!S-1!Personal", oneDrive, "OneDrive - Personal");

        var marks = Marks(
        [
            Copy(Path.Combine(oneDrive, "a.jpg")),
            Copy(Path.Combine(oneDrive, "b.jpg"), role: LocationRole.Reference),
            Copy(Path.Combine(Documents, "a.jpg")),
        ]);
        var files = Only(marks).Group.Files;

        Assert.Contains("OneDrive - Personal", marks.Keeping.WhyNotKept(files[0]));
        Assert.Null(marks.Keeping.WhyNotKept(files[1]));
        Assert.NotNull(marks.Keeping.CloudFolderOf(files[1]));
        Assert.Null(marks.Keeping.CloudFolderOf(files[2]));
    }

    [Fact]
    public void NoCopyCountsAsOutsideACloudFolderWhenTheSyncRootsCannotBeRead()
    {
        _cloud.RefusesToListRoots = true;

        var marks = Marks([Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "b.jpg"), role: LocationRole.Reference)]);
        var files = Only(marks).Group.Files;

        Assert.NotNull(marks.Keeping.WhyNotKept(files[0]));
        Assert.NotNull(marks.Keeping.CloudFolderOf(files[0]));
        Assert.Null(marks.Keeping.WhyNotKept(files[1]));
        Assert.Equal(0, marks.Run(new MarkingRule.KeepNewest()).Marked);
    }

    [Fact]
    public void AFileWithOneNameInTheTemporaryFolderAndAnotherInDocumentsCanBeKept()
    {
        var file = Copy(InTemp("a.jpg"), names: [InTemp("a.jpg"), Path.Combine(Documents, "a.jpg")]);
        var marks = Marks([file, Copy(Path.Combine(Downloads, "a.jpg"))]);

        Assert.Null(marks.Keeping.WhyNotKept(Only(marks).Group.Files[0]));
    }

    [Fact]
    public void KeepTheNewestKeepsTheNewestCopyThatCanBeKeptAndMarksANewerOneOnAUsbDrive()
    {
        var older = Copy(Path.Combine(Documents, "a.jpg"), Older);
        var newer = Copy(Path.Combine(Downloads, "a.jpg"), Newer);
        var newest = OnUsb("a.jpg", Newest);
        var marks = Marks([older, newer, newest]);

        var outcome = marks.Run(new MarkingRule.KeepNewest());
        var group = Only(marks);

        Assert.Equal(2, outcome.Marked);
        Assert.False(group.IsMarked(newer));
        Assert.True(group.IsMarked(older));
        Assert.True(group.IsMarked(newest));
    }

    [Fact]
    public void AReferenceCopyAndAFileWithSeveralNamesAreNeverMarkedAndCanBeKept()
    {
        var reference = Copy(Path.Combine(Documents, "Photos", "a.jpg"), role: LocationRole.Reference);
        var linked = Copy(Path.Combine(Downloads, "a.jpg"), names: [Path.Combine(Downloads, "a.jpg"), Path.Combine(Downloads, "b.jpg")]);
        var other = Copy(Path.Combine(Downloads, "c.jpg"));
        var marks = Marks([reference, linked, other]);
        var group = Only(marks);

        Assert.Contains("reference", group.Mark(reference, marks.Keeping));
        Assert.Contains("2 names", group.Mark(linked, marks.Keeping));
        Assert.Null(marks.Keeping.WhyNotKept(reference));
        Assert.Null(marks.Keeping.WhyNotKept(linked));

        marks.Run(new MarkingRule.KeepNewest());

        Assert.False(group.IsMarked(reference));
        Assert.False(group.IsMarked(linked));
    }

    [Fact]
    public void AnOnlineOnlyCopyAndACopyInAProgramFolderAreRefused()
    {
        var tool = Path.Combine(_tree.Top, "Apps", "Tool");
        _programs.Add(new ProgramFolder(tool, ReachedFolder.At(tool, _tree.Volumes), "Tool"));

        var online = Copy(Path.Combine(Documents, "a.dll"), storage: FileStorage.CloudOnly);
        var installed = Copy(Path.Combine(tool, "a.dll"));
        var marks = Marks([online, installed, Copy(Path.Combine(Downloads, "a.dll"))]);
        var group = Only(marks);

        Assert.Contains("online-only", group.Mark(online, marks.Keeping));
        Assert.Contains("'Tool' is installed here", group.Mark(installed, marks.Keeping));
        Assert.NotNull(marks.Keeping.WhyNotKept(online));
        Assert.NotNull(marks.Keeping.WhyNotKept(installed));
    }

    [Fact]
    public void EveryExploreRefusalApplies()
    {
        var inWindows = Copy(Path.Combine(_tree.System.WindowsDirectory, "System32", "a.dll"));
        var inProgramFiles = Copy(Path.Combine(_tree.System.ProgramFiles, "Vendor", "a.dll"));
        var otherAccount = Copy(Path.Combine(_tree.Users, "other", "Documents", "a.dll"));
        var mailStore = Copy(Path.Combine(Documents, "archive.pst"));
        var marks = Marks([inWindows, inProgramFiles, otherAccount, mailStore, Copy(Path.Combine(Downloads, "a.dll"))]);
        var policy = _tree.Policy();

        foreach (var refused in (DuplicateCandidate[])[inWindows, inProgramFiles, otherAccount, mailStore])
        {
            var verdict = policy.MayRemove(refused.Path);

            Assert.False(verdict.IsAllowed);
            Assert.Equal(verdict.Reason, Only(marks).Mark(refused, marks.Keeping));
            Assert.Equal(verdict.Reason, marks.Keeping.WhyNotKept(refused));
        }
    }

    [Fact]
    public void AToolRootsUnrecognisedChildIsRefused()
    {
        var gradle = Path.Combine(_tree.Environment.UserProfile, ".gradle");
        _policy = new ExploreActionPolicy(
            ProtectedRegions.For(_tree.System, _tree.Environment),
            [ToolRoot.Folders(gradle, "Gradle keeps its settings here.", name => name == "caches")],
            _tree.Volumes);
        var settings = Copy(Path.Combine(gradle, "init.d", "a.gradle"));
        var cached = Copy(Path.Combine(gradle, "caches", "a.gradle"));
        var marks = Marks([settings, cached, Copy(Path.Combine(Documents, "a.gradle"))]);

        Assert.Contains("not something Deguffer recognises", Only(marks).Mark(settings, marks.Keeping));
        Assert.Null(Only(marks).Mark(cached, marks.Keeping));
    }

    [Fact]
    public void EachRuleMarksWhatItSaysAndNeverACopyInACloudFolder()
    {
        var oneDrive = Path.Combine(_tree.Environment.UserProfile, "OneDrive");
        _cloud.Root("OneDrive!S-1!Personal", oneDrive, "OneDrive - Personal");
        var camera = Path.Combine(Documents, "Camera");

        DuplicateCandidate[] Group() =>
        [
            Copy(Path.Combine(Documents, "a.jpg"), Older),
            Copy(Path.Combine(camera, "longer name", "a.jpg"), Newer),
            Copy(Path.Combine(Downloads, "a.jpg"), Newest),
            Copy(Path.Combine(oneDrive, "a.jpg"), Newest.AddDays(1)),
        ];

        void Expect(MarkingRule rule, params int[] marked)
        {
            var files = Group();
            var marks = Marks(files);
            marks.Run(rule);

            Assert.Equal(marked, Enumerable.Range(0, files.Length).Where(index => Only(marks).IsMarked(files[index])).ToArray());
        }

        Expect(new MarkingRule.KeepNewest(), 0, 1);
        Expect(new MarkingRule.KeepOldest(), 1, 2);
        Expect(new MarkingRule.KeepShortestPath(), 1, 2);
        Expect(new MarkingRule.KeepInFolder(camera), 0, 2);
        Expect(new MarkingRule.MarkInFolder(camera), 1);
    }

    [Fact]
    public void NoRuleMarksWhileAReferenceLocationWentUnsearched()
    {
        _unsearchedReferences.Add(new UnsearchedLocation(new SearchLocation(@"X:\Photos", LocationRole.Reference), "Windows would not open it."));
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.jpg"), Older), Copy(Path.Combine(Downloads, "a.jpg"), Newer)];
        var marks = Marks(files);

        var outcome = marks.Run(new MarkingRule.KeepNewest());

        Assert.Equal(0, outcome.Marked);
        Assert.NotNull(outcome.Refused);
        Assert.False(Only(marks).IsMarked(files[0]));

        // A mark by hand is one the user looked at, and stays open.
        Assert.Null(Only(marks).Mark(files[0], marks.Keeping));
    }

    [Fact]
    public void TheFreeableSpaceCountsAReferenceARefusedAndAMultiNameCopyAsNothing()
    {
        var marks = Marks(
            [Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "a.jpg")), Copy(Path.Combine(Downloads, "b.jpg"))],
            [
                Copy(Path.Combine(Documents, "c.jpg"), role: LocationRole.Reference),
                Copy(Path.Combine(_tree.System.ProgramFiles, "c.jpg")),
                Copy(Path.Combine(Downloads, "c.jpg"), names: [Path.Combine(Downloads, "c.jpg"), Path.Combine(Downloads, "d.jpg")]),
                Copy(Path.Combine(Downloads, "e.jpg")),
            ]);

        // Three markable copies, one kept: two can go. A reference, a refused and a linked copy free
        // nothing, so of four only the last can go, and the reference is what is kept.
        Assert.Equal([2 * 4096L, 4096L], marks.Groups.Select(group => group.FreeableSpace(marks.Keeping)));
    }

    [Fact]
    public void AProgramsInstallLocationThatHoldsAChosenLocationIsStillAFolderACopyIsRefusedIn()
    {
        var tool = _tree.Folder("Apps", "Tool");
        var chosen = _tree.Folder("Apps", "Tool", "Assets");
        _tree.Registry.With(InstalledApps.UninstallScope.Machine64, "Tool", ("DisplayName", "Tool"), ("InstallLocation", tool));

        var reading = ProgramFolders.Read(
            _tree.Registry,
            _tree.Environment,
            _tree.System,
            _tree.Volumes,
            SearchLocations.Resolve([new SearchLocation(chosen)], _tree.Volumes).Locations,
            CancellationToken.None);

        Assert.DoesNotContain(reading.Folders, folder => folder.Program == "Tool");
        Assert.Contains(reading.SetAside, entry => entry.Program == "Tool");
        Assert.Contains(reading.Installed, folder => folder.Program == "Tool");
    }
}
