using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Testing;

/// <summary>
/// Groups and their marks as a search hands them to the Duplicates page, on a scratch drive whose
/// disks are internal, so the page's wiring can be tested without a search: every copy is in the
/// signed-in profile, where nothing refuses it, so each group could free what its copies occupy
/// with one left.
/// </summary>
public sealed class DuplicateScene : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly LocalVolume _volume;
    private int _files;

    public DuplicateScene()
    {
        Top = _temp.Path;
        Volumes = new FakeVolumeInventory().With(Top + Path.DirectorySeparatorChar);
        System = new FakeSystemDirectories(Top);
        Environment = new FakeUserEnvironment(Path.Combine(Top, "Users"));
        _volume = new LocalVolume(Top + Path.DirectorySeparatorChar, DriveType.Fixed, VolumeReadiness.Ready);
    }

    public string Top { get; }

    public FakeVolumeInventory Volumes { get; }

    public FakeSystemDirectories System { get; }

    public FakeUserEnvironment Environment { get; }

    /// <summary>A folder in the profile, where a copy is neither refused nor kept from being kept.</summary>
    public string Folder(string name) => Path.Combine(Environment.UserProfile, name);

    /// <summary>A copy at <paramref name="path"/>, as a search identified it.</summary>
    public DuplicateCandidate Copy(string path, long sizeOnDisk = 4096, LocationRole role = LocationRole.Search) =>
        new(
            new FileIdentity(1, (UInt128)(++_files)),
            path,
            Path.GetFileName(path),
            [path],
            1,
            Length: sizeOnDisk,
            SizeOnDisk: sizeOnDisk,
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FileStorage.Plain,
            role)
        {
            Volume = _volume,
        };

    /// <summary>Two copies named <paramref name="name"/>, in Documents and Downloads, each occupying <paramref name="sizeOnDisk"/>.</summary>
    public DuplicateGroup Pair(string name, long sizeOnDisk, MatchCriteria criteria = MatchCriteria.Content) =>
        Group(criteria, Copy(Path.Combine(Folder("Documents"), name), sizeOnDisk), Copy(Path.Combine(Folder("Downloads"), name), sizeOnDisk));

    /// <summary>A group of <paramref name="copies"/>, with an XXH128 checksum where the content matched.</summary>
    public static DuplicateGroup Group(MatchCriteria criteria, params DuplicateCandidate[] copies) =>
        new(
            criteria,
            copies[0].Length,
            criteria.ReadsContent() ? new ContentChecksum(ChecksumAlgorithm.XxHash128, new byte[16]) : null,
            copies);

    /// <summary>What finding the candidates of <paramref name="groups"/> found.</summary>
    public static CandidateFinding Finding(params DuplicateGroup[] groups) =>
        new(
            [.. groups.Select(group => new CandidateGroup(group.Length, Name: null, Modified: null, group.Files))],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            default);

    /// <summary>The marks a search makes once it has found <paramref name="finding"/>, holding no group yet.</summary>
    public DuplicateMarks Marks(CandidateFinding finding) =>
        DuplicateMarks.For(
            finding,
            new ExploreActionPolicy(ProtectedRegions.For(System, Environment), [], Volumes),
            [],
            Environment,
            new FakeCloudFiles(),
            Volumes,
            _ => new VolumeMedia(StorageMedia.Nvme, [0]),
            _ => new VolumeMedia(StorageMedia.Nvme, [0]),
            FileInformation.Default);

    public void Dispose() => _temp.Dispose();
}
