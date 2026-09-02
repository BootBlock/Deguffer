using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7's age for a directory, which four providers ask for and which was answered four ways until
/// this existed.
///
/// The column is the one §7 says drives the decision more than size does, so the direction an error
/// takes matters: an age that reads older than the truth invites a deletion, and one that reads
/// newer discourages it. Every case here is chosen for that reason rather than for coverage.
/// </summary>
public sealed class DirectoryAgeTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static readonly DateTime LongAgo = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    /// <summary>
    /// A file rewritten in place is caught, which the directory's own timestamp alone would miss.
    ///
    /// This is the case both of the removed copies were written for. NTFS moves a directory's
    /// timestamp when an entry is added, removed or renamed, and leaves it alone when an entry's
    /// contents change — so a workspace database SQLite rewrites daily, or a project rebuilt daily
    /// into an unchanged output layout, would report the date the layout last changed.
    /// </summary>
    [Fact]
    public void ReadsAnEntryRewrittenInPlace()
    {
        var directory = _temp.CreateDirectory("obj");
        _temp.CreateFile(16, "obj", "Example.dll");

        Directory.SetLastWriteTimeUtc(directory, LongAgo);
        var rewritten = DateTime.UtcNow.AddDays(-3);
        File.SetLastWriteTimeUtc(Path.Combine(directory, "Example.dll"), rewritten);

        Assert.Equal(rewritten, DirectoryAge.Of(directory)!.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// An entry added, removed or renamed is caught too, and that is what the entries alone miss.
    ///
    /// A build that prunes stale output touches the directory and nothing that stays in it. Reading
    /// only the entries reports the remaining ones' age, so a project cleaned this morning reads as
    /// over a year old — measured at exactly that on this machine before this case existed, which is
    /// the deletion-inviting direction.
    /// </summary>
    [Fact]
    public void ReadsTheDirectoryItselfWhenItsEntriesAreOlderThanIt()
    {
        var directory = _temp.CreateDirectory("obj");
        _temp.CreateFile(16, "obj", "left-behind.dll");
        _temp.CreateDirectory("obj", "Debug");

        File.SetLastWriteTimeUtc(Path.Combine(directory, "left-behind.dll"), LongAgo);
        Directory.SetLastWriteTimeUtc(Path.Combine(directory, "Debug"), LongAgo);

        var pruned = DateTime.UtcNow.AddMinutes(-5);
        Directory.SetLastWriteTimeUtc(directory, pruned);

        Assert.Equal(pruned, DirectoryAge.Of(directory)!.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// An emptied directory has an age, and it is the moment it was emptied.
    ///
    /// Reading only the entries has nothing to answer with here and says "unknown", which §7 renders
    /// as a blank column — so a project cleaned an hour ago is indistinguishable from one whose age
    /// could not be read at all.
    /// </summary>
    [Fact]
    public void AnEmptyDirectoryIsDatedByWhenItWasEmptied()
    {
        var directory = _temp.CreateDirectory("obj");
        var emptied = DateTime.UtcNow.AddHours(-1);
        Directory.SetLastWriteTimeUtc(directory, emptied);

        Assert.Equal(emptied, DirectoryAge.Of(directory)!.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// One level only. A directory whose own child directory holds the recent write reports the
    /// child directory's timestamp, not the file's, because this runs per project across a whole
    /// source root and descending would enumerate the tree it exists to avoid enumerating.
    /// </summary>
    [Fact]
    public void DoesNotDescend()
    {
        var directory = _temp.CreateDirectory("obj");
        var nested = _temp.CreateDirectory("obj", "Debug");
        var deep = _temp.CreateFile(16, "obj", "Debug", "net10.0", "Example.dll");

        Directory.SetLastWriteTimeUtc(directory, LongAgo);
        Directory.SetLastWriteTimeUtc(nested, LongAgo);
        File.SetLastWriteTimeUtc(deep, DateTime.UtcNow);

        Assert.Equal(LongAgo, DirectoryAge.Of(directory)!.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// A directory that is not there has no age. <see cref="DirectoryInfo.LastWriteTimeUtc"/>
    /// answers for a missing path with the start of the Windows epoch rather than failing, and
    /// reporting January 1601 as an age would be the oldest possible invitation to delete something
    /// that is already gone.
    /// </summary>
    [Fact]
    public void AMissingDirectoryHasNoAge()
    {
        Assert.Null(DirectoryAge.Of(Path.Combine(_temp.Path, "never-existed")));
    }

    /// <summary>§6.3: the same answer past MAX_PATH, where an unextended path cannot even open.</summary>
    [Fact]
    public void ReadsADirectoryPastMaxPath()
    {
        var deep = Path.Combine(_temp.Path, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(new string('d', 60), 5)), "obj");
        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "Example.dll")), new byte[16]);

        Assert.True(deep.Length > 260, "the fixture is not long enough to test anything");

        var rewritten = DateTime.UtcNow.AddDays(-9);
        Directory.SetLastWriteTimeUtc(LongPath.Extended(deep), LongAgo);
        File.SetLastWriteTimeUtc(LongPath.Extended(Path.Combine(deep, "Example.dll")), rewritten);

        Assert.Equal(rewritten, DirectoryAge.Of(deep)!.Value, TimeSpan.FromSeconds(1));
    }
}
