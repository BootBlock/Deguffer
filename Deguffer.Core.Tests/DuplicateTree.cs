using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A scratch drive for a duplicate search: a volume whose top is a scratch folder, Windows' own
/// folders and a Users folder at its top, and a signed-in profile inside that, so what a search
/// passes over is decided by the same rules it is on a real drive.
///
/// <para><b>Built at the scratch folder's final path.</b> The search resolves every location to the
/// path Windows gives an opened handle, and TEMP can be named through an 8.3 alias or a link. Every
/// fake here is rooted where the search will look, or the fakes and the search would be talking about
/// two spellings of one folder.</para>
/// </summary>
internal sealed class DuplicateTree : IDisposable
{
    private readonly TempDirectory _temp = new();

    public DuplicateTree()
    {
        Top = LongPath.Display(FileInformation.Default.FinalPath(_temp.Path)!);
        Volumes = new FakeVolumeInventory().With(Top + Path.DirectorySeparatorChar);
        System = new FakeSystemDirectories(Top);
        Environment = new FakeUserEnvironment(Path.Combine(Top, "Users"));
    }

    /// <summary>The top of the scratch volume.</summary>
    public string Top { get; }

    public FakeVolumeInventory Volumes { get; }

    public FakeSystemDirectories System { get; }

    public FakeUserEnvironment Environment { get; }

    public FakeUninstallRegistry Registry { get; } = new();

    /// <summary>The Users folder, which holds the signed-in profile and every other account's.</summary>
    public string Users => Path.Combine(Top, "Users");

    /// <summary>A file of <paramref name="bytes"/> bytes at <paramref name="segments"/> below the top.</summary>
    public string File(int bytes, params string[] segments)
    {
        var path = Path.Combine([Top, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        global::System.IO.File.WriteAllBytes(path, new byte[bytes]);

        return path;
    }

    /// <summary>A file holding <paramref name="content"/> at <paramref name="segments"/> below the top.</summary>
    public string File(byte[] content, params string[] segments)
    {
        var path = Path.Combine([Top, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        global::System.IO.File.WriteAllBytes(path, content);

        return path;
    }

    public string Folder(params string[] segments) =>
        Directory.CreateDirectory(Path.Combine([Top, .. segments])).FullName;

    public ExploreActionPolicy Policy() =>
        new(ProtectedRegions.For(System, Environment), [], Volumes);

    /// <summary>Search by walking, which is the route a test can run without administrator rights.</summary>
    /// <param name="files">Where each location is opened, for a test that has Windows refuse one.</param>
    public CandidateFinder Finder(ExploreScanner? scanner = null, FileInformation? files = null) =>
        new(scanner ?? new ExploreScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated)),
            Volumes, Registry, Environment, System, files ?? FileInformation.Default);

    /// <summary>
    /// The whole search, reading by walking. The drive's disks are not described, so its files are
    /// read one at a time, as on a disk Windows did not describe.
    /// </summary>
    /// <param name="read">Reads each file's content, for a test that counts, holds or stops the reads.</param>
    public DuplicateSearcher Searcher(ReadContent? read = null) =>
        new(Finder(), new VolumeMediaCache(new FakeStorageQueries()), read ?? ContentReader.Default.Read);

    public Task<CandidateFinding> FindAsync(DuplicateSearch search) => Finder().FindAsync(search, Policy());

    public Task<CandidateFinding> FindAsync(MatchCriteria criteria, params SearchLocation[] locations) =>
        FindAsync(new DuplicateSearch(criteria, locations));

    public void Dispose() => _temp.Dispose();
}
