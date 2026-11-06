using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

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

        EntryTime.Set(directory, LongAgo);
        var rewritten = DateTime.UtcNow.AddDays(-3);
        EntryTime.Set(Path.Combine(directory, "Example.dll"), created: LongAgo, written: rewritten);

        Assert.Equal(rewritten, DirectoryAge.Of(directory)!.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// An entry copied or extracted in is dated by when it arrived, not by the last write it kept from
    /// its source.
    ///
    /// <para>A package unpacked this morning keeps its publisher's last write, which can be years old,
    /// and only its creation time says it is new. Read by last write alone, the folder holding it reads
    /// years older than the truth, the direction that invites a deletion, and older than the
    /// minimum-age guard, which reads the same pair, judges its files.</para>
    /// </summary>
    [Fact]
    public void ReadsWhenAnEntryWasCopiedInRatherThanTheWriteItKept()
    {
        var directory = _temp.CreateDirectory("node_modules");
        var package = _temp.CreateDirectory("node_modules", "left-pad");

        var arrived = DateTime.UtcNow.AddHours(-2);
        EntryTime.Set(package, created: arrived, written: LongAgo);
        EntryTime.Set(directory, LongAgo);

        Assert.Equal(arrived, DirectoryAge.Of(directory)!.Value, TimeSpan.FromSeconds(1));
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

        EntryTime.Set(Path.Combine(directory, "left-behind.dll"), LongAgo);
        EntryTime.Set(Path.Combine(directory, "Debug"), LongAgo);

        var pruned = DateTime.UtcNow.AddMinutes(-5);
        EntryTime.Set(directory, created: LongAgo, written: pruned);

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
        EntryTime.Set(directory, created: LongAgo, written: emptied);

        Assert.Equal(emptied, DirectoryAge.Of(directory)!.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// One level only, and a child <em>directory</em> counts as an entry at that level.
    ///
    /// Both halves are asserted at once, by giving the child directory a date of its own between the
    /// parent's and the deep file's. The child's date must be the answer: reading files alone would
    /// return the parent's, and descending would return the file's. Files alone is the shape to
    /// guard against rather than an invented one — it is what the copy this rule replaced did, and
    /// <c>obj\Debug</c> is exactly the entry whose timestamp moves as output is added and removed.
    ///
    /// Descending is refused because this runs per project across a whole source root, and it would
    /// enumerate the tree discovery went out of its way not to enumerate.
    /// </summary>
    [Fact]
    public void ReadsAChildDirectoryAsAnEntryAndDoesNotDescendPastIt()
    {
        var directory = _temp.CreateDirectory("obj");
        var nested = _temp.CreateDirectory("obj", "Debug");
        var deep = _temp.CreateFile(16, "obj", "Debug", "net10.0", "Example.dll");

        var configurationChanged = DateTime.UtcNow.AddDays(-40);

        EntryTime.Set(directory, LongAgo);
        EntryTime.Set(nested, configurationChanged);
        EntryTime.Set(deep, DateTime.UtcNow);

        Assert.Equal(configurationChanged, DirectoryAge.Of(directory)!.Value, TimeSpan.FromSeconds(1));
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

    /// <summary>
    /// A directory whose entries cannot be read has no age at all.
    ///
    /// The rule is the newest of the directory <em>and</em> its entries, so a refusal leaves only
    /// the half that reads older than the truth. §5.3 makes the refusal ordinary; what it must not
    /// do is turn a partial reading into a number. The live subject is a servicing log directory the
    /// account may not list, where the traces inside are rewritten while the parent's timestamp sits
    /// still — and the row carrying that age is Tier 3, whose loss is permanent.
    /// </summary>
    [Fact]
    public void ARefusedDirectoryHasNoAge()
    {
        var directory = _temp.CreateDirectory("RtBackup");
        _temp.CreateFile(16, "RtBackup", "trace.etl");

        EntryTime.Set(Path.Combine(directory, "trace.etl"), DateTime.UtcNow);
        EntryTime.Set(directory, LongAgo);

        using var denied = new DeniedDirectory(directory);

        Assert.Null(DirectoryAge.Of(directory));
    }

    /// <summary>
    /// The newest entry inside still decides the age past <c>MAX_PATH</c>, which is exactly where a
    /// build directory's age tends to be read from.
    ///
    /// It is not evidence for §6.3, and the reason it once gave was wrong: an unextended path opens
    /// perfectly well at this depth, because .NET prefixes one of 260 characters or more itself. See
    /// <see cref="LongPathTests.TheRuntimeStillReachesPastMaxPathWithoutOurPrefix"/>.
    /// </summary>
    [Fact]
    public void ReadsADirectoryPastMaxPath()
    {
        var deep = Path.Combine(_temp.Path, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(new string('d', 60), 5)), "obj");
        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "Example.dll")), new byte[16]);

        Assert.True(deep.Length > 260, "the fixture is not long enough to test anything");

        var rewritten = DateTime.UtcNow.AddDays(-9);
        EntryTime.Set(LongPath.Extended(deep), LongAgo);
        EntryTime.Set(LongPath.Extended(Path.Combine(deep, "Example.dll")), rewritten);

        Assert.Equal(rewritten, DirectoryAge.Of(deep)!.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Dating a directory of ten thousand entries makes no object for each entry.
    ///
    /// <para>A <c>node_modules</c> holds thousands of entries at its top level, and a row dates every
    /// one it offers. Read through <see cref="FileSystemInfo"/>, each entry cost an object with its full
    /// path and its name, about three megabytes here, for one timestamp. The listing's own record of
    /// the entry carries the timestamp, so what is left is the listing itself.</para>
    /// </summary>
    [Fact]
    public void DatesTenThousandEntriesWithoutAnObjectForEach()
    {
        var directory = _temp.CreateDirectory("node_modules");

        for (var entry = 0; entry < 10_000; entry++)
        {
            File.Create(Path.Combine(directory, $"package-{entry:D5}")).Dispose();
        }

        // Once first, so what is measured is the dating rather than the first call's compilation.
        Assert.NotNull(DirectoryAge.Of(directory));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var age = DirectoryAge.Of(directory);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(age);
        Assert.True(allocated < 64 * 1024, $"Dating 10,000 entries allocated {allocated:N0} bytes.");
    }

    /// <summary>
    /// Every directory dated together keeps its own age, at its own place in the list, the absent and
    /// the refused included.
    ///
    /// <para>The ages differ by a day a directory, so an age handed to the wrong directory, by the
    /// threads finishing in a different order from the list's, reads as a failure rather than as a
    /// near miss.</para>
    /// </summary>
    [Fact]
    public void DatesEachOfManyDirectoriesAtItsOwnPlace()
    {
        const int Count = 48;
        const int Absent = 11;
        const int Refused = 29;

        var directories = new string[Count];
        var expected = new DateTime?[Count];

        for (var index = 0; index < Count; index++)
        {
            directories[index] = Path.Combine(_temp.Path, $"project-{index:D2}", "obj");

            if (index == Absent)
            {
                continue;
            }

            Directory.CreateDirectory(directories[index]);
            File.WriteAllBytes(Path.Combine(directories[index], "Example.dll"), new byte[16]);

            var written = LongAgo.AddDays(index);
            EntryTime.Set(Path.Combine(directories[index], "Example.dll"), written);
            EntryTime.Set(directories[index], LongAgo);
            expected[index] = index == Refused ? null : written;
        }

        using var denied = new DeniedDirectory(directories[Refused]);

        var ages = DirectoryAge.OfEach(directories, ScanTuner.Shipped, default);

        Assert.Equal(Count, ages.Count);

        for (var index = 0; index < Count; index++)
        {
            Assert.Equal(expected[index], ages[index]);
        }
    }

    /// <summary>
    /// Dating keeps to its volume's number of listings in flight, which the walks of every row planned
    /// beside it share. With the volume's every slot held, dating waits, and one slot given back lets
    /// it finish.
    /// </summary>
    [Fact]
    public async Task DatesNoMoreDirectoriesAtOnceThanItsVolumeIsTunedFor()
    {
        var directory = _temp.CreateDirectory("obj");
        var budget = new ListingBudget();
        var extended = LongPath.Extended(directory);
        var limit = ScanTuner.Shipped.For(directory).Walk.Threads;

        var held = new Stack<ListingBudget.Slot>();

        for (var slot = 0; slot < limit; slot++)
        {
            held.Push(budget.Take(extended, limit));
        }

        var dating = Task.Run(() => DirectoryAge.OfEach([directory], ScanTuner.Shipped, budget, default));

        Assert.True(SpinWait.SpinUntil(() => budget.Waiting(extended) == 1, TimeSpan.FromSeconds(10)), "Dating never waited for a slot.");
        Assert.False(dating.IsCompleted);

        held.Pop().Dispose();

        Assert.NotNull(Assert.Single(await dating.WaitAsync(TimeSpan.FromSeconds(10))));

        while (held.Count > 0)
        {
            held.Pop().Dispose();
        }

        Assert.Equal(0, budget.InFlight(extended));
    }
}
