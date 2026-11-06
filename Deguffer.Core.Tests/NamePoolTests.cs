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
}
