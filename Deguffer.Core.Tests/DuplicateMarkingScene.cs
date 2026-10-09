using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The scene §7.4's marking tests share: groups built as a search would hand them over, on a scratch
/// drive whose own folders decide what Explore's policy refuses, beside drives standing for a USB
/// disk that Windows reports as fixed, a disk of no known kind, a rotational, a solid-state and a
/// virtual disk. What a drive's disks are is read through the media cache from the bus each reports,
/// as on a real machine.
/// </summary>
public abstract class DuplicateMarkingScene : IDisposable
{
    private protected const string Usb = @"U:\";

    private protected const string Unknown = @"V:\";

    private protected const string Rotational = @"R:\";

    private protected const string SolidState = @"S:\";

    private protected const string Virtual = @"W:\";

    private protected static readonly DateTime Older = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private protected static readonly DateTime Newer = Older.AddDays(1);
    private protected static readonly DateTime Newest = Older.AddDays(2);

    private protected readonly DuplicateTree _tree = new();
    private protected readonly FakeCloudFiles _cloud = new();
    private protected readonly List<StorageClean> _cleans = [];
    private protected readonly List<ProgramFolder> _programs = [];
    private protected readonly List<UnsearchedLocation> _unsearchedReferences = [];
    private protected readonly LocalVolume _internal;
    private protected readonly LocalVolume _usb = new(Usb, DriveType.Fixed, VolumeReadiness.Ready);
    private protected readonly FakeStorageQueries _queries;
    private protected readonly VolumeMediaCache _media;
    private int _files;

    protected DuplicateMarkingScene()
    {
        // Where Windows puts it, inside the profile, so no refusal of the folder holding it answers first.
        _tree.Environment.WithTempPath(Path.Combine(_tree.Environment.LocalAppData, "Temp"));
        _tree.Volumes.With(Usb).With(Unknown).With(Rotational).With(SolidState).With(Virtual);
        _internal = new LocalVolume(_tree.Top + Path.DirectorySeparatorChar, DriveType.Fixed, VolumeReadiness.Ready);
        _media = new VolumeMediaCache(_queries = new FakeStorageQueries()
            .Volume(_internal.RootPath, 0).Disk(0, bus: 0x11, seekPenalty: null)
            .Volume(Usb, 1).Disk(1, bus: 0x07, seekPenalty: null)
            .Volume(Unknown, 2).Disk(2, bus: 0x0B, seekPenalty: null)
            .Volume(Rotational, 3).Disk(3, bus: 0x0B, seekPenalty: true)
            .Volume(SolidState, 4).Disk(4, bus: 0x0B, seekPenalty: false)
            .Volume(Virtual, 5).Disk(5, bus: 0x0E, seekPenalty: null));
    }

    public void Dispose()
    {
        _tree.Dispose();
        GC.SuppressFinalize(this);
    }

    private protected string Documents => Path.Combine(_tree.Environment.UserProfile, "Documents");

    private protected string Downloads => Path.Combine(_tree.Environment.UserProfile, "Downloads");

    private protected DuplicateCandidate Copy(
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

    private protected DuplicateCandidate OnUsb(string name, DateTime? modified = null, LocationRole role = LocationRole.Search) =>
        Copy(Path.Combine(Usb, "Backup", name), modified, role, _usb);

    private protected ExploreActionPolicy? _policy;

    private protected DuplicateSearchResult Result(params DuplicateCandidate[][] groups) =>
        new(
            new CandidateFinding([], [], _unsearchedReferences, [], [], [], [], _programs, [], default),
            [.. groups.Select(files => new DuplicateGroup(MatchCriteria.Content, 100, Checksum: null, files))],
            default,
            Stopped: false);

    private protected DuplicateMarks Marks(params DuplicateCandidate[][] groups) => MarksOf(Result(groups));

    private protected DuplicateMarks MarksOf(DuplicateSearchResult result) =>
        DuplicateMarks.For(
            result,
            _policy ?? _tree.Policy(),
            _cleans,
            _tree.Environment,
            _cloud,
            _tree.Volumes,
            _media.Of,
            _media.Now,
            FileInformation.Default);

    private protected static GroupMarks Only(DuplicateMarks marks) => Assert.Single(marks.Groups);

    /// <summary>What this machine's protections would be with <paramref name="providers"/> as Storage's.</summary>
    private protected Task<MachineProtections> Protections(params ICleanupProvider[] providers) =>
        MachineProtections.ForAsync(_tree.System, _tree.Environment, _tree.Volumes, _tree.Registry, providers);

    private protected ProgramFolderReading ReadProgramFolders(params string[] chosen) => ReadProgramFolders(FileInformation.Default, chosen);

    private protected ProgramFolderReading ReadProgramFolders(FileInformation files, params string[] chosen) =>
        ProgramFolders.Read(
            _tree.Registry,
            _tree.Environment,
            _tree.System,
            _tree.Volumes,
            files,
            SearchLocations.Resolve([.. chosen.Select(folder => new SearchLocation(folder))], _tree.Volumes).Locations,
            CancellationToken.None);

    private protected string InTemp(string name) => Path.Combine(_tree.Environment.TempPath, name);
}
