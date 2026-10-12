using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which route reads a file's link count and sizes: by name where the file system answers that, a
/// handle where it does not, and the same figure from either, through
/// <see cref="FileInformation.Standard"/>.
/// </summary>
public sealed class LinkCountRouteTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private readonly List<string> _askedByName = [];
    private readonly List<string> _opened = [];

    public void Dispose() => _temp.Dispose();

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
        readByName: (string path, out FileInformation.FileStandardInfo info) =>
        {
            lock (_askedByName)
            {
                _askedByName.Add(path);
            }

            if (answer is { } given)
            {
                info = default;
                return given;
            }

            return StatByName.Read(path, out info);
        });

    [Fact]
    public void AFileReadByNameOpensNoHandle()
    {
        var file = LongPath.Extended(_temp.CreateFile(4096, "a.bin"));

        var info = Answering(answer: null).Standard(file, new ByNameRoute());

        Assert.Equal(4096, info?.EndOfFile);
        Assert.Equal([file], _askedByName);
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
        Assert.Equal([first], _askedByName);
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
        Assert.Equal([first, second], _askedByName);
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
    /// files asks by name once before it turns to handles. §6.3 by the form of each path: both
    /// routes are given it extended.
    /// </summary>
    [Fact]
    public async Task AScanMeasuresTheSameFigureByEitherRoute()
    {
        var store = _temp.CreateDirectory("store");
        _temp.CreateFile(4096, "store", "a", "sole.bin");
        _temp.CreateFile(10_000, "store", "b", "sole.bin");
        var shared = _temp.CreateFile(65536, "store", "b", "shared.bin");
        HardLink.To(shared, Path.Combine(_temp.CreateDirectory("project"), "linked.bin"));

        var byName = await new HardLinkAwareScanner(ScanTuner.Shipped, Answering(answer: null)).MeasureAsync(store);
        Assert.Empty(_opened);
        Assert.Equal(3, _askedByName.Count);
        _askedByName.Clear();

        var byHandle = await new HardLinkAwareScanner(ScanTuner.Shipped, Answering(ByNameReading.Unsupported)).MeasureAsync(store);

        Assert.Equal(14_096, byName.Size.Logical);
        Assert.Equal(byName.Size, byHandle.Size);
        Assert.Single(_askedByName);
        Assert.Equal(3, _opened.Count);
        Assert.All(_opened, path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));
        Assert.StartsWith(@"\\?\", _askedByName[0], StringComparison.Ordinal);
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
