using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which route reads a file's link count and sizes: by name where the file system answers that, a
/// handle where it does not, and the same figure from either, through
/// <see cref="FileInformation.Standard"/>.
/// </summary>
public sealed class LinkCountRouteTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private readonly List<AskedByName> _askedByName = [];
    private readonly List<string> _opened = [];

    public void Dispose() => _temp.Dispose();

    /// <summary>One read by name, as it reached the reader.</summary>
    /// <param name="Directory">The handle the name was given relative to, or zero.</param>
    /// <param name="Name">The name as given: a bare name where <paramref name="Directory"/> is a handle.</param>
    /// <param name="Folder">
    /// Where <paramref name="Directory"/> is open on, read from Windows while the read ran, or null
    /// where the name was given on its own.
    /// </param>
    private sealed record AskedByName(nint Directory, string Name, string? Folder);

    /// <summary>Information whose by-name read answers <paramref name="answer"/>, or reads the disk where it is null.</summary>
    private FileInformation Answering(ByNameReading? answer) => new(
        (path, use) =>
        {
            lock (_opened)
            {
                _opened.Add(path);
            }

            return FileInformation.Open(path, use);
        },
        FileInformation.ReadIdentity,
        readByName: (nint directory, ReadOnlySpan<char> name, out FileInformation.FileStandardInfo info) =>
        {
            var asked = new AskedByName(directory, name.ToString(), directory == 0 ? null : FolderOf(directory));
            lock (_askedByName)
            {
                _askedByName.Add(asked);
            }

            if (answer is { } given)
            {
                info = default;
                return given;
            }

            return StatByName.Read(directory, name, out info);
        });

    /// <summary>Where the walk's handle is open on, asked without taking it over.</summary>
    private static string? FolderOf(nint directory)
    {
        using var borrowed = new SafeFileHandle(directory, ownsHandle: false);

        return FileInformation.FinalPathOf(borrowed);
    }

    /// <summary>The name a single file is asked by on its own: its full path in the object manager's form.</summary>
    private static AskedByName OnItsOwn(string extendedPath) => new(0, LongPath.ObjectManagerForm(extendedPath), null);

    [Fact]
    public void AFileReadByNameOpensNoHandle()
    {
        var file = LongPath.Extended(_temp.CreateFile(4096, "a.bin"));

        var info = Answering(answer: null).Standard(file, new ByNameRoute());

        Assert.Equal(4096, info?.EndOfFile);
        Assert.Equal([OnItsOwn(file)], _askedByName);
        Assert.StartsWith(@"\??\", _askedByName[0].Name, StringComparison.Ordinal);
        Assert.Empty(_opened);
    }

    /// <summary>
    /// A file system that does not take the class by name, as one that predates it may not. The file
    /// is still measured, through a handle, and the walk asks by name no more.
    /// </summary>
    [Fact]
    public void AFileSystemThatDoesNotAnswerByNameIsReadThroughAHandleForTheRestOfTheWalk()
    {
        var first = LongPath.Extended(_temp.CreateFile(4096, "a.bin"));
        var second = LongPath.Extended(_temp.CreateFile(8192, "b.bin"));
        var files = Answering(ByNameReading.Unsupported);
        var route = new ByNameRoute();

        Assert.Equal(4096, files.Standard(first, route)?.EndOfFile);
        Assert.Equal(8192, files.Standard(second, route)?.EndOfFile);

        Assert.True(route.Declined);
        Assert.Equal([OnItsOwn(first)], _askedByName);
        Assert.Equal([first, second], _opened);
    }

    /// <summary>One file refused by name is asked through a handle, and the next file is asked by name again.</summary>
    [Fact]
    public void AFileRefusedByNameIsReadThroughAHandleAndTheWalkKeepsAsking()
    {
        var first = LongPath.Extended(_temp.CreateFile(4096, "a.bin"));
        var second = LongPath.Extended(_temp.CreateFile(8192, "b.bin"));
        var files = Answering(ByNameReading.Refused);
        var route = new ByNameRoute();

        Assert.Equal(4096, files.Standard(first, route)?.EndOfFile);
        Assert.Equal(8192, files.Standard(second, route)?.EndOfFile);

        Assert.False(route.Declined);
        Assert.Equal([OnItsOwn(first), OnItsOwn(second)], _askedByName);
        Assert.Equal([first, second], _opened);
    }

    [Fact]
    public void AFileGoneByNameIsNotAskedAgainThroughAHandle()
    {
        var absent = LongPath.Extended(Path.Combine(_temp.Path, "nowhere.bin"));

        Assert.Null(Answering(answer: null).Standard(absent, new ByNameRoute()));
        Assert.Empty(_opened);
    }

    /// <summary>
    /// The scan's figure is the same whichever route its file system leaves it, and a walk of several
    /// files stops asking by name once it is declined. §6.3 by the form of each path: a handle is
    /// opened by the full extended path.
    ///
    /// <para>The walk reads its folders on several threads, so a thread that asked before another's
    /// answer was in may ask once too. One folder's files are read in turn on one thread, so no folder
    /// asks twice: <c>b</c> holds two files, and only its first is asked.</para>
    /// </summary>
    [Fact]
    public async Task AScanMeasuresTheSameFigureByEitherRoute()
    {
        var store = CreateStore();

        var byName = await new HardLinkAwareScanner(ScanTuner.Shipped, Answering(answer: null)).MeasureAsync(store);
        Assert.Empty(_opened);
        Assert.Equal(3, _askedByName.Count);
        _askedByName.Clear();

        var byHandle = await new HardLinkAwareScanner(ScanTuner.Shipped, Answering(ByNameReading.Unsupported)).MeasureAsync(store);

        Assert.Equal(14_096, byName.Size.Logical);
        Assert.Equal(byName.Size, byHandle.Size);
        Assert.NotEmpty(_askedByName);
        Assert.All(_askedByName.GroupBy(asked => asked.Folder), folder => Assert.Single(folder));
        Assert.Equal(3, _opened.Count);
        Assert.All(_opened, path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));
    }

    /// <summary>
    /// Each file the scan walks is named by its bare name, relative to a handle open on the folder it
    /// was listed in, so neither its full path nor its name for the object manager is built.
    /// </summary>
    [Fact]
    public async Task AScanReadsEachFileByItsBareNameRelativeToTheFolderItWasListedIn()
    {
        var store = CreateStore();

        await new HardLinkAwareScanner(ScanTuner.Shipped, Answering(answer: null)).MeasureAsync(store);

        Assert.Equal(
            [("a", "sole.bin"), ("b", "shared.bin"), ("b", "sole.bin")],
            _askedByName.Select(asked => (Path.GetFileName(asked.Folder)!, asked.Name)).Order());
        Assert.All(_askedByName, asked => Assert.NotEqual(0, asked.Directory));
    }

    /// <summary>
    /// A file refused by name relative to its folder is still measured, through a handle opened by its
    /// full path in the extended form (§6.3), and gives the figure the read by name gives.
    /// </summary>
    [Fact]
    public async Task AFileRefusedByNameRelativeToItsFolderIsReadThroughAHandleOpenedByItsFullPath()
    {
        var store = CreateStore();
        var byName = await new HardLinkAwareScanner(ScanTuner.Shipped, Answering(answer: null)).MeasureAsync(store);
        _askedByName.Clear();

        var refused = await new HardLinkAwareScanner(ScanTuner.Shipped, Answering(ByNameReading.Refused)).MeasureAsync(store);

        Assert.Equal(byName.Size, refused.Size);
        Assert.Equal(3, _askedByName.Count);
        Assert.Equal(
            [
                LongPath.Extended(Path.Combine(store, "a", "sole.bin")),
                LongPath.Extended(Path.Combine(store, "b", "shared.bin")),
                LongPath.Extended(Path.Combine(store, "b", "sole.bin")),
            ],
            _opened.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A store of three files. <c>b\shared.bin</c> has a second name outside the store, so it holds
    /// none of the store's own bytes.
    /// </summary>
    private string CreateStore()
    {
        var store = _temp.CreateDirectory("store");
        _temp.CreateFile(4096, "store", "a", "sole.bin");
        _temp.CreateFile(10_000, "store", "b", "sole.bin");
        var shared = _temp.CreateFile(65536, "store", "b", "shared.bin");
        HardLink.To(shared, Path.Combine(_temp.CreateDirectory("project"), "linked.bin"));

        return store;
    }

    /// <summary>A single named file is read by the same route, and falls back the same way.</summary>
    [Fact]
    public async Task ASingleFileIsMeasuredThroughAHandleWhereItsFileSystemDoesNotAnswerByName()
    {
        var file = _temp.CreateFile(8192, "loose", "blob.bin");

        var result = await new HardLinkAwareScanner(ScanTuner.Shipped, Answering(ByNameReading.Unsupported)).MeasureAsync(file);

        Assert.Equal(8192, result.Size.Logical);
        Assert.Equal([LongPath.Extended(file)], _opened);
    }
}
