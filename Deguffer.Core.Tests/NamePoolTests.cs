using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

/// <summary>
/// The one store of every name an Explore tree holds: each distinct name numbered once, read back
/// exactly, readable through a frozen view while more names are added, and shared by every parse
/// thread of a file-table read.
/// </summary>
public class NamePoolTests
{
    [Fact]
    public void NumbersANameMetAgainAsTheFirstTime()
    {
        var pool = new NamePool();

        var first = pool.Intern("index.js");
        var other = pool.Intern("package.json");
        var again = pool.Intern("index.js".AsSpan());

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);

        // The empty name and the two above, and no copy of the one met twice.
        Assert.Equal(3, pool.Count);
    }

    /// <summary>
    /// A directory with case sensitivity enabled holds <c>Cache</c> and <c>cache</c> as two names, so
    /// the pool keeps each as the disk holds it.
    /// </summary>
    [Fact]
    public void KeepsNamesThatDifferOnlyInCaseApart()
    {
        var pool = new NamePool();

        var upper = pool.Intern("Cache");
        var lower = pool.Intern("cache");
        var names = pool.Freeze();

        Assert.NotEqual(upper, lower);
        Assert.Equal("Cache", names[upper].ToString());
        Assert.Equal("cache", names[lower].ToString());
    }

    [Fact]
    public void NumbersTheEmptyNameAsTheSlotNoReaderFilled()
    {
        var pool = new NamePool();

        Assert.Equal(NamePool.Empty, pool.Intern(string.Empty));
        Assert.True(pool.Freeze()[NamePool.Empty].IsEmpty);
    }

    /// <summary>
    /// A snapshot of a walk reads the names frozen when it was taken while the walk adds more, so
    /// growing the pool past many chunks must leave every name an earlier view covers where it was.
    /// </summary>
    [Fact]
    public void AnEarlierViewReadsEveryNameItCoversAfterThePoolGrows()
    {
        var pool = new NamePool();
        var early = Enumerable.Range(0, 1_000).Select(i => pool.Intern($"early-{i}.txt")).ToArray();
        var frozen = pool.Freeze();

        // Several hundred thousand characters, well past one chunk and past the first chunk directory.
        for (var i = 0; i < 40_000; i++)
        {
            pool.Intern($"later-file-name-{i}.dat");
        }

        for (var i = 0; i < early.Length; i++)
        {
            Assert.Equal($"early-{i}.txt", frozen[early[i]].ToString());
        }

        Assert.Equal(early.Length + 1, frozen.Count);

        var now = pool.Freeze();
        Assert.Equal("later-file-name-39999.dat", now[now.Count - 1].ToString());
        Assert.Equal("early-999.txt", now[early[^1]].ToString());
    }

    /// <summary>
    /// A scan's root carries the path the user chose, which can be longer than one chunk of the
    /// pool. It is kept whole, and the names around it are unharmed.
    /// </summary>
    [Fact]
    public void KeepsANameLongerThanAChunkWhole()
    {
        var pool = new NamePool();
        var before = pool.Intern("before");
        var longName = @"\\?\C:\" + new string('a', 40_000);
        var number = pool.Intern(longName);
        var after = pool.Intern("after");
        var names = pool.Freeze();

        Assert.Equal(longName, names[number].ToString());
        Assert.Equal("before", names[before].ToString());
        Assert.Equal("after", names[after].ToString());
        Assert.Equal(number, pool.Intern(longName));
    }

    /// <summary>
    /// #486: every parse thread interns into one pool without a lock of its own, so threads meeting
    /// the same names at once, while the pool grows under them many times, must still give each
    /// distinct name exactly one number. <see cref="ExploreTree.CompareByName(int[], NamePool.Frozen, int, int)"/>
    /// takes two equal numbers as one name without comparing them, so a name numbered twice would be
    /// sorted as two different names, and two names given one number would be drawn as one.
    /// </summary>
    [Fact]
    public void NumbersEachNameOnceWhenManyThreadsInternTheSameNamesAtOnce()
    {
        const int Threads = 8;
        const int Distinct = 20_000;

        var pool = new NamePool();
        var names = Enumerable.Range(0, Distinct).Select(i => $"name-{i % 97}-{i}.js").ToArray();
        var numbers = new int[Threads][];
        using var start = new Barrier(Threads);

        var threads = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
        {
            // Each thread meets the names in its own order, so a name is new to the pool on whichever
            // thread happens to reach it first, and the others find it or race to add it.
            var order = Enumerable.Range(0, Distinct).OrderBy(i => (i * (t + 3) + t * 7919) % Distinct).ToArray();
            var mine = new int[Distinct];

            start.SignalAndWait();

            foreach (var i in order)
            {
                mine[i] = pool.Intern(names[i]);
            }

            numbers[t] = mine;
        })).ToArray();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        for (var t = 1; t < Threads; t++)
        {
            Assert.Equal(numbers[0], numbers[t]);
        }

        Assert.Equal(Distinct, numbers[0].Distinct().Count());
        Assert.DoesNotContain(NamePool.Empty, numbers[0]);

        // The empty name and each distinct name once, with nothing added twice.
        Assert.Equal(Distinct + 1, pool.Count);

        var frozen = pool.Freeze();
        for (var i = 0; i < Distinct; i++)
        {
            Assert.Equal(names[i], frozen[numbers[0][i]].ToString());
            Assert.Equal(numbers[0][i], pool.Intern(names[i]));
        }
    }

    /// <summary>
    /// #552: a tree of a whole volume uses all but a handful of the names its reader numbered, and
    /// copying 1.4 million names to drop one cost a system volume 98 MiB and a fifth of a second. A
    /// tree that uses nearly every name keeps the pool as it was, with its numbers untouched, and
    /// spends only the bit a name it takes to find which are used.
    /// </summary>
    [Fact]
    public void KeepingNearlyEveryNameHandsThePoolBackAndAllocatesOnlyABitAName()
    {
        const int Distinct = 100_000;

        KeepEveryName(NumberedNames(Distinct));

        var (names, numbers) = NumberedNames(Distinct);
        var nameNumbers = KeepEveryName((names, numbers)).NameNumbers;
        var asRead = nameNumbers.ToArray();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var kept = NamePool.Keeping(names, nameNumbers);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(names.Count, kept.Count);
        Assert.Equal(asRead, nameNumbers);
        Assert.Equal("name-041234.dat", kept[nameNumbers[41_233]].ToString());

        // From the bits alone, so a measurement that missed the large object heap, where a copy's
        // map would be, fails rather than passing at nothing. The bound leaves an array's header.
        var bits = ((names.Count + 63) / 64) * sizeof(ulong);
        Assert.InRange(allocated, bits, bits + 32);

        // Every name but the one left out is used by two nodes, as a name repeats on a disk. Called
        // once untimed so that the pass is compiled before it is measured.
        static (NamePool.Frozen Kept, int[] NameNumbers) KeepEveryName((NamePool.Frozen Names, int[] Numbers) pool)
        {
            int[] nameNumbers = [.. pool.Numbers, .. pool.Numbers];
            return (NamePool.Keeping(pool.Names, nameNumbers), nameNumbers);
        }
    }

    /// <summary>
    /// #552: a copy of the few names a folder's tree uses costs those names and a bit and a count
    /// for each of the pool's names, not a map from every name in the pool to its new number, which
    /// at four bytes a name was 5 MB for a system volume however few names were kept.
    /// </summary>
    [Fact]
    public void KeepingFewNamesAllocatesNoMapTheSizeOfThePool()
    {
        const int Distinct = 200_000;
        const int UsedEvery = 100;

        KeepFew(NumberedNames(Distinct));

        var (names, numbers) = NumberedNames(Distinct);
        var nameNumbers = numbers.Where((_, i) => i % UsedEvery == 0).ToArray();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var kept = NamePool.Keeping(names, nameNumbers);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1 + (Distinct / UsedEvery), kept.Count);
        Assert.Equal("name-000101.dat", kept[nameNumbers[1]].ToString());

        // A map would be four bytes a name. Half that leaves the bits, the counts, and the copy's
        // first chunk of characters and of entries, whatever the few names kept.
        Assert.InRange(allocated, 1, names.Count * 2L);

        // Called once untimed so that the pass is compiled before it is measured.
        static void KeepFew((NamePool.Frozen Names, int[] Numbers) pool) =>
            NamePool.Keeping(pool.Names, pool.Numbers.Where((_, i) => i % UsedEvery == 0).ToArray());
    }

    /// <summary>
    /// #364: a tree of one folder keeps the names its nodes use and no other. Each is copied in the
    /// order the pool numbered it, every node's number is rewritten to its name's number in the copy,
    /// and a node with no name keeps the empty name. The names used lie either side of the 64-name
    /// steps a node's new number is counted in, the first of a step among them.
    /// </summary>
    [Fact]
    public void KeepingFewNamesCopiesOnlyThoseUsedInTheOrderTheyWereNumbered()
    {
        var pool = new NamePool();
        var numbers = Enumerable.Range(0, 200).Select(i => pool.Intern($"file-{i:D3}.txt")).ToArray();
        var names = pool.Freeze();
        int[] nameNumbers = [numbers[7], numbers[2], numbers[7], NamePool.Empty, numbers[5], numbers[150], numbers[70], numbers[63]];

        var kept = NamePool.Keeping(names, nameNumbers);

        Assert.Equal(64, numbers[63]);
        Assert.Equal(7, kept.Count);
        Assert.Equal([3, 1, 3, NamePool.Empty, 2, 6, 5, 4], nameNumbers);
        Assert.Equal(
            ["", "file-002.txt", "file-005.txt", "file-007.txt", "file-063.txt", "file-070.txt", "file-150.txt"],
            Enumerable.Range(0, kept.Count).Select(number => kept[number].ToString()));
    }

    /// <summary>
    /// #552: a copy is made only where the names left out hold more than the names kept, counting
    /// each name's characters and its entry, so a tree never keeps more than twice what its names
    /// need. The weight is the text, not the count: one long name kept against several short ones
    /// left out keeps the pool.
    /// </summary>
    [Theory]
    [InlineData(new[] { "aaaa", "bbbb", "cccc", "dddd" }, new[] { 0 }, true)]
    [InlineData(new[] { "aaaa", "bbbb", "cccc", "dddd" }, new[] { 0, 1 }, false)]
    [InlineData(new[] { "a-name-long-enough-to-hold-more-than-every-short-one-together", "b", "c", "d", "e", "f" }, new[] { 0 }, false)]
    [InlineData(new[] { "a", "b", "c", "d", "e", "a-name-long-enough-to-hold-more-than-every-short-one-together" }, new[] { 0 }, true)]
    public void KeepingCopiesOnlyWhereTheNamesLeftOutHoldMoreThanTheNamesKept(string[] held, int[] usedIndices, bool copied)
    {
        var pool = new NamePool();
        var numbers = held.Select(name => pool.Intern(name)).ToArray();
        var names = pool.Freeze();
        var nameNumbers = usedIndices.Select(index => numbers[index]).ToArray();

        var kept = NamePool.Keeping(names, nameNumbers);

        Assert.Equal(copied ? 1 + usedIndices.Length : names.Count, kept.Count);
        Assert.Equal(usedIndices.Select(index => held[index]), nameNumbers.Select(number => kept[number].ToString()));
    }

    /// <summary>
    /// A pool of <paramref name="distinct"/> names, <c>name-000001.dat</c> onwards, and one more no
    /// node uses, with each of the first's numbers in order.
    /// </summary>
    private static (NamePool.Frozen Names, int[] Numbers) NumberedNames(int distinct)
    {
        var pool = new NamePool();
        var numbers = new int[distinct];

        for (var i = 0; i < distinct; i++)
        {
            numbers[i] = pool.Intern($"name-{i + 1:D6}.dat");
        }

        pool.Intern("left-out.dat");
        return (pool.Freeze(), numbers);
    }
}
