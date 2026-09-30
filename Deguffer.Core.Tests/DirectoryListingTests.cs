using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The one listing every walk and classification in Core reads a directory through, and the three
/// answers it gives without throwing.
///
/// <para>Each of those answers used to be an exception caught one frame up. The results were right,
/// and a scan of a whole drive still threw one for every protected folder on it, which is what a
/// debugger showed as a flood. So each test here asserts both halves: the answer, and that reaching
/// it threw nothing.</para>
/// </summary>
public sealed class DirectoryListingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ListsEveryEntryHiddenOnesIncludedAndSaysSo()
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateDirectory("root", "child");
        var hidden = _temp.CreateFile(8, "root", "hidden.bin");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var (entries, outcome, thrown) = List(root);

        Assert.Equal(["child", "hidden.bin"], entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(PathPresence.Present, outcome);
        Assert.Empty(thrown);
    }

    [Fact]
    public void ADirectoryThatIsNotThereIsAbsentAndThrowsNothing()
    {
        var (entries, outcome, thrown) = List(Path.Combine(_temp.Path, "never-created"));

        Assert.Equal(PathPresence.Absent, outcome);
        Assert.Empty(entries);
        Assert.Empty(thrown);
    }

    /// <summary>
    /// A directory under one that is not there is absent too. Windows answers
    /// <c>ERROR_PATH_NOT_FOUND</c> for it rather than the <c>ERROR_FILE_NOT_FOUND</c> a missing leaf
    /// gets, and both have to land on the same answer.
    /// </summary>
    [Fact]
    public void ADirectoryUnderOneThatIsNotThereIsAbsentToo()
    {
        var (_, outcome, thrown) = List(Path.Combine(_temp.Path, "never-created", "below"));

        Assert.Equal(PathPresence.Absent, outcome);
        Assert.Empty(thrown);
    }

    [Fact]
    public void ADirectoryThatWillNotBeListedIsRefusedAndThrowsNothing()
    {
        var root = _temp.CreateDirectory("refused");
        _temp.CreateFile(8, "refused", "inside.bin");

        using var denied = new DeniedDirectory(root);

        var (entries, outcome, thrown) = List(root);

        Assert.Equal(PathPresence.Refused, outcome);
        Assert.Empty(entries);
        Assert.Empty(thrown);
    }

    /// <summary>
    /// A file where a directory was expected is refused rather than absent: something is there, and
    /// it is not what anybody could list. Every listing this replaced answered it that way, through
    /// the <see cref="IOException"/> .NET raises for <c>ERROR_DIRECTORY</c>.
    /// </summary>
    [Fact]
    public void AFileIsRefusedRatherThanAbsent()
    {
        var (entries, outcome, thrown) = List(_temp.CreateFile(8, "a-file.bin"));

        Assert.Equal(PathPresence.Refused, outcome);
        Assert.Empty(entries);
        Assert.Empty(thrown);
    }

    /// <summary>
    /// §6.3. Every caller turns an entry straight into a path to descend into or act on, so the entry
    /// has to carry the prefix, and it does because the listing extends the directory it was given.
    /// </summary>
    [Fact]
    public void HandsBackEntriesInTheExtendedLengthForm()
    {
        var root = _temp.CreateDirectory("root");
        _temp.CreateFile(8, "root", "leaf.bin");

        var (entries, _, _) = List(root);

        Assert.StartsWith(@"\\?\", Assert.Single(entries).FullName, StringComparison.Ordinal);
    }

    private static (IReadOnlyList<FileSystemInfo> Entries, PathPresence Outcome, IReadOnlyList<Exception> Thrown) List(string directory)
    {
        List<FileSystemInfo> entries = [];
        var outcome = PathPresence.Present;

        var thrown = ThrownExceptions.During(() =>
        {
            using var listing = DirectoryListing.Of(directory);

            while (listing.MoveNext())
            {
                entries.Add(listing.Current);
            }

            outcome = listing.Outcome;
        });

        return (entries, outcome, thrown);
    }
}
