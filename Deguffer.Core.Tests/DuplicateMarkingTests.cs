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

    private const string Rotational = @"R:\";

    private const string SolidState = @"S:\";

    private const string Virtual = @"W:\";

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
        _tree.Volumes.With(Usb).With(Unknown).With(Rotational).With(SolidState).With(Virtual);
        _internal = new LocalVolume(_tree.Top + Path.DirectorySeparatorChar, DriveType.Fixed, VolumeReadiness.Ready);
        _media = new VolumeMediaCache(new FakeStorageQueries()
            .Volume(_internal.RootPath, 0).Disk(0, bus: 0x11, seekPenalty: null)
            .Volume(Usb, 1).Disk(1, bus: 0x07, seekPenalty: null)
            .Volume(Unknown, 2).Disk(2, bus: 0x0B, seekPenalty: null)
            .Volume(Rotational, 3).Disk(3, bus: 0x0B, seekPenalty: true)
            .Volume(SolidState, 4).Disk(4, bus: 0x0B, seekPenalty: false)
            .Volume(Virtual, 5).Disk(5, bus: 0x0E, seekPenalty: null));
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
        IReadOnlyList<string>? names = null,
        long sizeOnDisk = 4096) =>
        new(
            new FileIdentity(1, (UInt128)(++_files)),
            path,
            Path.GetFileName(path),
            names ?? [path],
            names?.Count ?? 1,
            Length: 100,
            SizeOnDisk: sizeOnDisk,
            modified ?? Older,
            storage,
            role)
        {
            Volume = volume ?? _internal,
        };

    private DuplicateCandidate OnUsb(string name, DateTime? modified = null, LocationRole role = LocationRole.Search) =>
        Copy(Path.Combine(Usb, "Backup", name), modified, role, _usb);

    private ExploreActionPolicy? _policy;

    private DuplicateSearchResult Result(params DuplicateCandidate[][] groups) =>
        new(
            new CandidateFinding([], [], _unsearchedReferences, [], [], [], [], _programs, [], default),
            [.. groups.Select(files => new DuplicateGroup(MatchCriteria.Content, 100, Checksum: null, files))],
            default,
            Stopped: false);

    private DuplicateMarks Marks(params DuplicateCandidate[][] groups) => MarksOf(Result(groups));

    private DuplicateMarks MarksOf(DuplicateSearchResult result) =>
        DuplicateMarks.For(
            result,
            _policy ?? _tree.Policy(),
            _cleans,
            _tree.Environment,
            _cloud,
            _tree.Volumes,
            _media.Of,
            FileInformation.Default);

    private static GroupMarks Only(DuplicateMarks marks) => Assert.Single(marks.Groups);

    /// <summary>What this machine's protections would be with <paramref name="providers"/> as Storage's.</summary>
    private Task<MachineProtections> Protections(params ICleanupProvider[] providers) =>
        MachineProtections.ForAsync(_tree.System, _tree.Environment, _tree.Volumes, providers);

    private ProgramFolderReading ReadProgramFolders(params string[] chosen) =>
        ProgramFolders.Read(
            _tree.Registry,
            _tree.Environment,
            _tree.System,
            _tree.Volumes,
            FileInformation.Default,
            SearchLocations.Resolve([.. chosen.Select(folder => new SearchLocation(folder))], _tree.Volumes).Locations,
            CancellationToken.None);

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

    /// <summary>
    /// A mark made while its group could keep another copy is judged again at the confirmation, as
    /// the machine is then: here the unmarked copy's folder became the temporary folder after the mark
    /// was made, so the group would keep only a copy that can go without anyone choosing it to.
    /// </summary>
    [Fact]
    public async Task AConfirmationJudgesTheMarksAgainAndKeepsNoneThatWouldLeaveNothingToKeep()
    {
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "a.jpg"))];
        var marks = Marks(files);
        var group = Only(marks);
        Assert.Null(group.Mark(files[0], marks.Keeping));
        Assert.Equal([files[0]], group.Standing(marks.Keeping));

        _tree.Environment.WithTempPath(Downloads);
        var confirmation = await RemovalConfirmation.ForAsync(marks, await Protections(), ExploreRemovalMode.RecycleBin, _ => null);

        Assert.True(group.IsMarked(files[0]));
        Assert.Empty(confirmation.Copies);
        Assert.Equal(0, confirmation.Space);
        Assert.Contains("temporary folder", marks.Keeping.WhyNotKept(files[1]));
    }

    /// <summary>
    /// Where Storage's cleans delete is asked of the providers again at the confirmation, through the
    /// protections built for it, so a place a clean names after the marks were made counts.
    /// </summary>
    [Fact]
    public async Task AConfirmationAsksStorageAgainWhereItsCleansDelete()
    {
        DuplicateCandidate[] files = [Copy(Path.Combine(Documents, "a.jpg")), Copy(Path.Combine(Downloads, "a.jpg"))];
        var provider = new FakeCleanupProvider("downloads") { Name = "Old downloads" };
        var marks = await DuplicateMarks.ForAsync(Result(files), await Protections(provider), _tree.Environment, _cloud, _tree.Volumes, _media);
        var group = Only(marks);
        Assert.Null(group.Mark(files[0], marks.Keeping));

        provider.Cleaned = [CleanedPlace.Whole(Downloads)];
        var confirmation = await RemovalConfirmation.ForAsync(marks, await Protections(provider), ExploreRemovalMode.RecycleBin, _ => null);

        Assert.Empty(confirmation.Copies);
        Assert.Contains("'Old downloads'", marks.Keeping.WhyNotKept(files[1]));
    }

    /// <summary>
    /// The places a provider declares, with its name, reach the keeping rule through the protections
    /// a page builds, not only through a list a test hands in.
    /// </summary>
    [Fact]
    public async Task WhereStoragesCleansDeleteReachesTheKeepingRuleWithTheCleansName()
    {
        var cache = Path.Combine(_tree.Environment.LocalAppData, "Fake");
        var provider = new FakeCleanupProvider("fake") { Name = "Fake cache", Cleaned = [CleanedPlace.Whole(cache)] };
        var protections = await Protections(provider);

        Assert.Equal([new StorageClean(CleanedPlace.Whole(cache), "Fake cache")], await protections.StorageCleansAsync());

        var cached = Copy(Path.Combine(cache, "a.bin"));
        var marks = await DuplicateMarks.ForAsync(
            Result([cached, Copy(Path.Combine(Documents, "a.bin"))]), protections, _tree.Environment, _cloud, _tree.Volumes, _media);

        Assert.Contains("Storage's 'Fake cache' clean", marks.Keeping.WhyNotKept(cached));
    }

    [Fact]
    public void TheGroupsAreSortedByTheSpaceEachCouldFree()
    {
        DuplicateCandidate[] Pair(string name, long size) =>
            [Copy(Path.Combine(Documents, name), sizeOnDisk: size), Copy(Path.Combine(Downloads, name), sizeOnDisk: size)];

        var marks = Marks(Pair("a.jpg", 4096), Pair("b.jpg", 8192), Pair("c.jpg", 16384));

        Assert.Equal(["c.jpg", "b.jpg", "a.jpg"], marks.Groups.Select(group => group.Group.Files[0].Name));
        Assert.Equal([16384L, 8192L, 4096L], marks.Groups.Select(group => group.FreeableSpace(marks.Keeping)));
    }

    /// <summary>The oldest copy here is not the one with the shortest path, so the two rules keep different copies.</summary>
    [Fact]
    public void KeepTheOldestAndKeepTheShortestPathKeepDifferentCopies()
    {
        var oldest = Copy(Path.Combine(Documents, "Camera", "longer name", "a.jpg"), Older);
        var shortest = Copy(Path.Combine(Downloads, "a.jpg"), Newer);

        var byAge = Marks([oldest, shortest]);
        byAge.Run(new MarkingRule.KeepOldest());
        var byPath = Marks([oldest, shortest]);
        byPath.Run(new MarkingRule.KeepShortestPath());

        Assert.False(Only(byAge).IsMarked(oldest));
        Assert.True(Only(byAge).IsMarked(shortest));
        Assert.False(Only(byPath).IsMarked(shortest));
        Assert.True(Only(byPath).IsMarked(oldest));
    }

    /// <summary>A drive is internal by its disks: rotational and solid state are, and a virtual disk, which can be detached, is not.</summary>
    [Theory]
    [InlineData(Rotational, StorageMedia.Rotational, true)]
    [InlineData(SolidState, StorageMedia.SolidState, true)]
    [InlineData(Virtual, StorageMedia.Virtual, false)]
    public void ACopyCountsAsKeptOutsideAReferenceOnlyOnAnInternalDrive(string root, StorageMedia media, bool kept)
    {
        var volume = new LocalVolume(root, DriveType.Fixed, VolumeReadiness.Ready);
        Assert.Equal(media, _media.Of(volume).Class);

        var marks = Marks([Copy(Path.Combine(root, "a.jpg"), volume: volume), Copy(Path.Combine(Documents, "a.jpg"))]);

        Assert.Equal(kept, marks.Keeping.WhyNotKept(Only(marks).Group.Files[0]) is null);
    }

    /// <summary>
    /// A search's paths are final paths, so a program installed at a path with a junction on the way
    /// is at the folder the junction leads to, and a copy found there is refused.
    /// </summary>
    [Fact]
    public void ACopyIsRefusedInAProgramsFolderReachedThroughAJunction()
    {
        var real = _tree.Folder("Real", "Apps");
        var apps = Path.Combine(_tree.Top, "Apps");
        Junction.ToDirectory(apps, real);
        var tool = _tree.Folder("Real", "Apps", "Tool");
        _tree.Registry.With(InstalledApps.UninstallScope.Machine64, "Tool", ("DisplayName", "Tool"), ("InstallLocation", Path.Combine(apps, "Tool")));
        _programs.AddRange(ReadProgramFolders().Installed);

        var installed = Copy(Path.Combine(tool, "a.dll"));
        var marks = Marks([installed, Copy(Path.Combine(Downloads, "a.dll"))]);

        Assert.Contains("'Tool' is installed here", marks.Keeping.Refusals.WhyRefused(installed));
    }

    /// <summary>
    /// An install location is judged where it leads as well as as it is named: one that is a junction
    /// to the profile has named the profile, which is neither passed over nor refused in.
    /// </summary>
    [Fact]
    public void AnInstallLocationThatLeadsToTheProfileIsSetAside()
    {
        Directory.CreateDirectory(_tree.Environment.UserProfile);
        var named = Path.Combine(_tree.Folder("Apps"), "Tool");
        Junction.ToDirectory(named, _tree.Environment.UserProfile);
        _tree.Registry.With(InstalledApps.UninstallScope.Machine64, "Tool", ("DisplayName", "Tool"), ("InstallLocation", named));

        var reading = ReadProgramFolders();

        Assert.Contains(reading.SetAside, entry => entry.Program == "Tool");
        Assert.DoesNotContain(reading.Installed, folder => folder.Program == "Tool");
    }

    /// <summary>
    /// A copy in a program's folder that a search went into is refused: one searched because the
    /// search includes the places it passes over by default, and one set aside because it holds a
    /// location the search was asked to search.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACopyInAProgramsFolderTheSearchWentIntoIsRefused(bool setAside)
    {
        var tool = _tree.Folder("Apps", "Tool");
        _tree.Registry.With(InstalledApps.UninstallScope.Machine64, "Tool", ("DisplayName", "Tool"), ("InstallLocation", tool));
        var installed = _tree.File(100, "Apps", "Tool", "Assets", "a.dll");
        _tree.File(100, "Data", "a.dll");
        var data = new SearchLocation(Path.Combine(_tree.Top, "Data"));
        SearchLocation[] locations = setAside
            ? [new SearchLocation(Path.Combine(tool, "Assets")), data]
            : [new SearchLocation(Path.Combine(_tree.Top, "Apps")), data];

        var result = await _tree.Searcher().SearchAsync(
            new DuplicateSearch(MatchCriteria.Size, locations, searchPassedOverPlaces: !setAside), _tree.Policy());
        var marks = MarksOf(result);

        Assert.Equal(setAside, result.Finding.SetAside.Any(entry => entry.Program == "Tool"));
        var copy = Assert.Single(Only(marks).Group.Files, file => file.Path == installed);
        Assert.Contains("'Tool' is installed here", marks.Keeping.Refusals.WhyRefused(copy));
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
        _programs.Add(new ProgramFolder(tool, ReachedFolder.At(tool, _tree.Volumes), "Tool", Final: null));

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

        var reading = ReadProgramFolders(chosen);

        Assert.DoesNotContain(reading.Folders, folder => folder.Program == "Tool");
        Assert.Contains(reading.SetAside, entry => entry.Program == "Tool");
        Assert.Contains(reading.Installed, folder => folder.Program == "Tool");
    }
}
