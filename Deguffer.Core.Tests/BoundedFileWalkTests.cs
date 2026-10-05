using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.3's other half, which had no test in either scanner until the walk became one seam.
///
/// "Treat 'access denied' as normal and skip silently — a locked file is the OS protecting live
/// state" is a safety rule, and an untested one is a rule that can be deleted without anything
/// noticing: removing the catch filter outright left the whole suite green. It is tested here
/// rather than in a scanner's own class because both scanners now reach it through
/// <see cref="BoundedFileWalk"/>, so one test covers both.
/// </summary>
public sealed class BoundedFileWalkTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The scan reports what it could read and does not fail, which is §5.3 exactly. Both halves
    /// matter: an exception here would take a whole preview down over one protected folder, and
    /// counting the unreadable subtree would promise bytes no deletion could reclaim.
    /// </summary>
    [Fact]
    public async Task ARefusedDirectoryIsSkippedAndTheRestOfTheTreeStillCounts()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "readable.bin");
        var refused = _temp.CreateDirectory("cache", "refused");
        _temp.CreateFile(65536, "cache", "refused", "unreachable.bin");

        using var denied = new DeniedDirectory(refused);

        var measured = await ParallelEnumerationScanner.Default.MeasureAsync(root);

        Assert.Equal(4096, measured.Size.Logical);
    }

    /// <summary>
    /// §6.3, and the claim <see cref="BoundedFileWalk.Visit"/> makes in its own parameter
    /// documentation: every path it hands back carries the prefix, whichever form the root was
    /// given in.
    ///
    /// <para>That claim is the one thing about the walk a long-path fixture can actually
    /// discriminate. Asserting that a deep tree was measured proves nothing — .NET
    /// prefixes past 260 characters on its own, so such a test passes with the prefixing deleted
    /// outright. <see cref="LongPathTests.TheRuntimeStillReachesPastMaxPathWithoutOurPrefix"/> is
    /// where that is established.</para>
    ///
    /// <para>Both forms are asserted. The plain one is what discriminates: an entry's path is built
    /// from the directory it was listed in, so a root the walk did not extend leaves every path below
    /// it without the prefix, however deep the tree runs.</para>
    /// </summary>
    [Fact]
    public void HandsBackEveryFileInTheExtendedLengthFormWhicheverFormTheRootHad()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(64, "cache", "top.bin");
        _temp.CreateFile(64, "cache", "nested", "deeper", "leaf.bin");

        Assert.All(Visited(LongPath.Extended(root)), p => Assert.StartsWith(@"\\?\", p, StringComparison.Ordinal));
        Assert.All(Visited(root), p => Assert.StartsWith(@"\\?\", p, StringComparison.Ordinal));
    }

    /// <summary>
    /// The state-carrying overload hands each directory back whatever its parent chose for it, which
    /// is how a caller building a structure keeps its place. Nothing else in the callback says which
    /// directory is being read, so a state delivered to the wrong child produces a tree that is
    /// entirely well formed and describes a different disk.
    /// </summary>
    [Fact]
    public void CarriesTheCallersStateDownToTheChildItWasChosenFor()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(16, "cache", "one", "a.bin");
        _temp.CreateFile(16, "cache", "one", "deeper", "b.bin");
        _temp.CreateFile(16, "cache", "two", "c.bin");

        var seen = new ConcurrentBag<(string State, string Entries)>();

        Walk(root, "cache", (state, contents, descend) =>
        {
            seen.Add((state, string.Join(", ", contents.Entries.Select(e => e.Name).Order(StringComparer.Ordinal))));

            foreach (var entry in contents.Entries.Where(e => e.IsDirectory))
            {
                descend(entry, entry.Name);
            }
        });

        Assert.Equal(
            [("cache", "one, two"), ("deeper", "b.bin"), ("one", "a.bin, deeper"), ("two", "c.bin")],
            seen.Order());
    }

    /// <summary>
    /// A link is reported and is never something the caller may descend into. The rule lives in the
    /// walk rather than in each of its callers, so it is asserted here: the link is present among the
    /// links, and the file inside its target is visited exactly once — through the target's own place
    /// in the tree, and not again through the name pointing at it.
    ///
    /// <para>The callback descends into the link itself, which is the mistake the walk exists to
    /// refuse. A caller that only ever iterates <see cref="DirectoryContents.Entries"/> would pass
    /// with the rule deleted.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void ReportsALinkSeparatelyAndNeverDescendsThroughIt(DirectoryLinkKind kind)
    {
        var root = _temp.CreateDirectory("cache");
        var real = _temp.CreateDirectory("cache", "content-v2");
        _temp.CreateFile(32, "cache", "content-v2", "inside.bin");

        DirectoryLink.Create(kind, Path.Combine(root, "shortcut"), real);

        var entries = new ConcurrentBag<string>();
        var links = new ConcurrentBag<string>();

        Walk<byte>(root, 0, (_, contents, descend) =>
        {
            foreach (var entry in contents.Entries)
            {
                entries.Add(entry.Name);

                if (entry.IsDirectory)
                {
                    descend(entry, 0);
                }
            }

            foreach (var link in contents.Links)
            {
                links.Add(link.Name);
                descend(link, 0);
            }
        });

        Assert.Equal(["shortcut"], links.Order(StringComparer.Ordinal));
        Assert.Equal(["content-v2", "inside.bin"], entries.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// §5.3 again, from the other side. A directory that could not be listed is still skipped
    /// silently, but it is now distinguishable from one that is genuinely empty — without that, a
    /// caller reporting a total has no way to know the total is a lower bound, and reports it as a
    /// measurement.
    /// </summary>
    [Fact]
    public void SaysWhichDirectoryItWasRefusedRatherThanReportingItEmpty()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateDirectory("cache", "empty");
        var refused = _temp.CreateDirectory("cache", "refused");
        _temp.CreateFile(64, "cache", "refused", "unreachable.bin");

        using var denied = new DeniedDirectory(refused);

        var outcomes = new ConcurrentBag<(string Directory, bool Refused, int Entries)>();

        Walk(root, "cache", (state, contents, descend) =>
        {
            outcomes.Add((state, contents.WasRefused, contents.Entries.Count));

            foreach (var entry in contents.Entries.Where(e => e.IsDirectory))
            {
                descend(entry, entry.Name);
            }
        });

        Assert.Equal(
            [("cache", false, 2), ("empty", false, 0), ("refused", true, 0)],
            outcomes.Order());
    }

    /// <summary>
    /// §6.3 for the state-carrying overload, asserted the same discriminating way as for the plain
    /// one: what a long-path fixture can actually prove is the form of the paths, not that a deep
    /// tree was reached.
    /// </summary>
    [Fact]
    public void HandsBackEveryEntryTheStatefulWalkReachesInTheExtendedLengthForm()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(64, "cache", "top.bin");
        _temp.CreateFile(64, "cache", "nested", "deeper", "leaf.bin");

        Assert.All(Reached(LongPath.Extended(root)), p => Assert.StartsWith(@"\\?\", p, StringComparison.Ordinal));
        Assert.All(Reached(root), p => Assert.StartsWith(@"\\?\", p, StringComparison.Ordinal));
    }

    /// <summary>
    /// The thread count and the listing buffer change how fast the walk runs and never what it finds.
    /// Every directory, every entry with every field the callers read, every link and every refusal
    /// is the same with one worker and with many, and with the smallest buffer and the largest.
    ///
    /// <para>One directory holds more entries than the smallest buffer can return in one call, so
    /// that listing takes many calls at one size and one at the other. A listing that dropped or
    /// repeated entries across calls would differ there.</para>
    /// </summary>
    [Fact]
    public void FindsTheSameTreeWithAnyThreadCountAndListingBuffer()
    {
        var root = _temp.CreateDirectory("cache");

        for (var i = 0; i < 1500; i++)
        {
            _temp.CreateFile(i % 7, "cache", "wide", $"entry-with-a-longer-name-{i:D4}.bin");
        }

        for (var branch = 0; branch < 6; branch++)
        {
            for (var depth = 1; depth <= 4; depth++)
            {
                var segments = new[] { "cache", $"branch-{branch}" }
                    .Concat(Enumerable.Range(1, depth).Select(d => $"level-{d}"))
                    .Append($"file-{depth}.bin")
                    .ToArray();

                _temp.CreateFile(depth * 10, segments);
            }
        }

        var target = _temp.CreateDirectory("cache", "branch-0", "level-1");
        Junction.ToDirectory(Path.Combine(root, "shortcut"), target);
        var refused = _temp.CreateDirectory("cache", "branch-1", "refused");
        _temp.CreateFile(64, "cache", "branch-1", "refused", "unreachable.bin");

        using var denied = new DeniedDirectory(refused);

        // NTFS brings the times a parent's index keeps for a folder up to date lazily, and opening the
        // folder is one thing that does it, so the first walk of a new tree can read a time the second
        // reads newer. One walk first leaves every walk compared below reading the same disk.
        Describe(root, WalkTuning.Default);

        var oneWorkerSmallest = Describe(root, new WalkTuning(1, WalkTuning.MinimumListingBuffer));

        Assert.Equal(1500, oneWorkerSmallest[Path.Combine(LongPath.Extended(root), "wide")].Split('\n').Length - 1);
        Assert.Contains(oneWorkerSmallest, directory => directory.Value.StartsWith("refused", StringComparison.Ordinal));

        AssertSameTree(oneWorkerSmallest, Describe(root, new WalkTuning(1, WalkTuning.MaximumListingBuffer)));
        AssertSameTree(oneWorkerSmallest, Describe(root, new WalkTuning(WalkTuning.MaximumThreads, WalkTuning.MinimumListingBuffer)));
        AssertSameTree(oneWorkerSmallest, Describe(root, new WalkTuning(WalkTuning.MaximumThreads, WalkTuning.MaximumListingBuffer)));
    }

    /// <summary>The same directories, each described the same, naming the first that is not.</summary>
    private static void AssertSameTree(SortedDictionary<string, string> expected, SortedDictionary<string, string> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);

        foreach (var (directory, described) in expected)
        {
            Assert.True(described == actual[directory], $"{directory} differs:\n{described}\n---\n{actual[directory]}");
        }
    }

    /// <summary>
    /// No worker waits for a directory it is not reading. The walk once read a whole level and waited
    /// for all of it before the next, so one slow directory held up every other worker, and the tree
    /// below its siblings was not reached until it finished.
    ///
    /// <para>Here the slow directory will not finish until a directory three levels below its sibling
    /// has been read. A walk with a barrier at each level never reaches that one while the slow
    /// directory is held, and the wait gives up.</para>
    /// </summary>
    [Fact]
    public void ReadsDeeperDirectoriesWhileASlowOneIsStillBeingRead()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateDirectory("cache", "slow");
        _temp.CreateFile(8, "cache", "quick", "one", "two", "leaf.bin");

        using var leafRead = new ManualResetEventSlim();
        var reachedWhileHeld = false;

        Walk(root, "cache", (state, contents, descend) =>
        {
            if (state == "slow")
            {
                reachedWhileHeld = leafRead.Wait(TimeSpan.FromSeconds(10));
            }

            if (state == "two")
            {
                leafRead.Set();
            }

            foreach (var entry in contents.Entries.Where(e => e.IsDirectory))
            {
                descend(entry, entry.Name);
            }
        }, new WalkTuning(2, WalkTuning.MinimumListingBuffer));

        Assert.True(reachedWhileHeld, "The deeper directory was not read while the slow one was held.");
    }

    /// <summary>
    /// §5.5's partial totals come from the progress callback, which has no levels to mark time by
    /// now, so it is timed: once as soon as there is anything to report, then once per interval, and
    /// once at the end with the finished totals.
    ///
    /// <para>The clock is the test's, so the cadence is asserted rather than raced. Left alone, a
    /// four-directory walk reports after its first directory and at its end. Moved on an interval
    /// by each report, it reports after every directory.</para>
    /// </summary>
    [Fact]
    public void ReportsProgressOnTheClocksIntervalAndAtTheEnd()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(8, "cache", "one", "two", "three", "leaf.bin");

        Assert.Equal(2, Reports(root, new ManualTimeProvider(), advanceBy: TimeSpan.Zero));
        Assert.Equal(2, Reports(root, new ManualTimeProvider(), advanceBy: BoundedFileWalk.ProgressInterval - TimeSpan.FromTicks(1)));
        Assert.Equal(5, Reports(root, new ManualTimeProvider(), advanceBy: BoundedFileWalk.ProgressInterval));
    }

    /// <summary>
    /// A caller's progress closure keeps its own state without a lock, as <see cref="Exploring.ExploreScanner"/>
    /// does with its snapshot clock, because progress once came between levels on one thread. With
    /// many workers, two of them due at once must still report one after the other.
    /// </summary>
    [Fact]
    public void NeverReportsProgressBesideItself()
    {
        var root = _temp.CreateDirectory("cache");

        for (var i = 0; i < 200; i++)
        {
            _temp.CreateFile(8, "cache", $"folder-{i}", "file.bin");
        }

        var clock = new ManualTimeProvider();
        var inside = 0;
        var overlapped = 0;
        var reports = 0;

        BoundedFileWalk.Visit<byte>(
            root,
            0,
            new WalkTuning(WalkTuning.MaximumThreads, WalkTuning.MinimumListingBuffer),
            (_, contents, descend) =>
            {
                // Every worker finds a report due, so every one of them reaches for the lock.
                clock.Advance(BoundedFileWalk.ProgressInterval);

                foreach (var entry in contents.Entries.Where(e => e.IsDirectory))
                {
                    descend(entry, 0);
                }
            },
            () =>
            {
                if (Interlocked.Increment(ref inside) > 1)
                {
                    Interlocked.Exchange(ref overlapped, 1);
                }

                Thread.Sleep(1);
                reports++;
                Interlocked.Decrement(ref inside);
            },
            clock,
            default);

        Assert.Equal(0, overlapped);
        Assert.True(reports > 1, $"Only {reports} reports were made, so none could have overlapped.");
    }

    /// <summary>
    /// G4: a walk the user cannot abandon is a bug. Cancelled from inside the walk, so what is under
    /// test is a walk already running: a chain six directories deep stops at the one that was being
    /// read, and reports no further progress.
    ///
    /// <para>Every directory moves the clock on by an interval, so a report is due after each one,
    /// the one that cancelled included. A report there would describe a walk that is not going to
    /// finish.</para>
    /// </summary>
    [Fact]
    public void StopsWhenCancelledPartWayAndReportsNothingMore()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(8, "cache", "l1", "l2", "l3", "l4", "l5", "file.bin");

        using var cancel = new CancellationTokenSource();
        var clock = new ManualTimeProvider();
        var read = new ConcurrentBag<string>();
        var reports = 0;

        Assert.ThrowsAny<OperationCanceledException>(() => BoundedFileWalk.Visit(
            root,
            "cache",
            WalkTuning.Default,
            (state, contents, descend) =>
            {
                read.Add(state);
                clock.Advance(BoundedFileWalk.ProgressInterval);

                if (state == "l2")
                {
                    cancel.Cancel();
                }

                foreach (var entry in contents.Entries.Where(e => e.IsDirectory))
                {
                    descend(entry, entry.Name);
                }
            },
            () => reports++,
            clock,
            cancel.Token));

        Assert.Equal(["cache", "l1", "l2"], read.Order(StringComparer.Ordinal));
        Assert.Equal(2, reports);
    }

    /// <summary>
    /// A directory's children are queued only once its report has been made. A report then
    /// describes the walk up to that directory, and a child read while its parent's report is still
    /// being made cannot find the next report not yet due and skip it, which is what made a timed
    /// cadence uneven.
    ///
    /// <para>The report holds on, with workers free to take anything queued. None takes the child.
    /// </para>
    /// </summary>
    [Fact]
    public void QueuesADirectorysChildrenOnlyOnceItsReportIsMade()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(8, "cache", "child", "file.bin");

        var childRead = 0;
        var childReadDuringReport = false;
        var reports = 0;

        BoundedFileWalk.Visit(
            root,
            "cache",
            new WalkTuning(4, WalkTuning.MinimumListingBuffer),
            (state, contents, descend) =>
            {
                if (state == "child")
                {
                    Interlocked.Exchange(ref childRead, 1);
                }

                foreach (var entry in contents.Entries.Where(e => e.IsDirectory))
                {
                    descend(entry, entry.Name);
                }
            },
            () =>
            {
                if (reports++ == 0)
                {
                    Thread.Sleep(200);
                    childReadDuringReport = Volatile.Read(ref childRead) == 1;
                }
            },
            new ManualTimeProvider(),
            default);

        Assert.False(childReadDuringReport, "The child was read while its parent's report was being made.");
        Assert.Equal(1, childRead);
    }

    /// <summary>
    /// A failure in a caller's callback reaches the caller as it was thrown, from whichever worker
    /// met it, and only once every other worker has left its callback. A helper runs on a pool
    /// thread, where an exception nobody carries back ends the process.
    /// </summary>
    [Fact]
    public void ThrowsACallbacksFailureOnceEveryWorkerHasStopped()
    {
        var root = _temp.CreateDirectory("cache");

        for (var i = 0; i < 40; i++)
        {
            _temp.CreateFile(8, "cache", $"folder-{i:D2}", "file.bin");
        }

        var failure = new InvalidOperationException("The callback failed.");
        var inside = 0;

        var thrown = Assert.Throws<InvalidOperationException>(() => Walk(root, "cache", (state, contents, descend) =>
        {
            Interlocked.Increment(ref inside);
            try
            {
                if (state == "folder-20")
                {
                    throw failure;
                }

                Thread.Sleep(5);

                foreach (var entry in contents.Entries.Where(e => e.IsDirectory))
                {
                    descend(entry, entry.Name);
                }
            }
            finally
            {
                Interlocked.Decrement(ref inside);
            }
        }, new WalkTuning(8, WalkTuning.MinimumListingBuffer)));

        Assert.Same(failure, thrown);
        Assert.Equal(0, Volatile.Read(ref inside));
    }

    [Theory]
    [InlineData(WalkTuning.MinimumThreads - 1, WalkTuning.MinimumListingBuffer)]
    [InlineData(WalkTuning.MaximumThreads + 1, WalkTuning.MinimumListingBuffer)]
    [InlineData(WalkTuning.MinimumThreads, WalkTuning.MinimumListingBuffer - 1)]
    [InlineData(WalkTuning.MinimumThreads, WalkTuning.MaximumListingBuffer + 1)]
    public void RefusesAThreadCountOrBufferOutsideItsBounds(int threads, int bufferBytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WalkTuning(threads, bufferBytes));

    /// <summary>
    /// Every directory the walk reads, by its path, as one string per directory holding everything a
    /// caller could read of it. Ordered, so the comparison is of what was found and not of the order
    /// in which workers found it.
    /// </summary>
    private static SortedDictionary<string, string> Describe(string root, WalkTuning tuning)
    {
        var found = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        BoundedFileWalk.Visit(
            root,
            LongPath.Extended(root),
            tuning,
            (path, contents, descend) =>
            {
                var described = new List<string> { contents.WasRefused ? "refused" : "listed" };
                described.AddRange(contents.Entries.Select(e => Line("entry", e)).Order(StringComparer.Ordinal));
                described.AddRange(contents.Links.Select(e => Line("link", e)).Order(StringComparer.Ordinal));
                described.AddRange(contents.ReparseFiles.Select(e => Line("marked", e)).Order(StringComparer.Ordinal));

                Assert.True(found.TryAdd(path, string.Join('\n', described)), $"A directory was read twice.");

                foreach (var entry in contents.Entries.Where(e => e.IsDirectory))
                {
                    descend(entry, entry.FullName);
                }
            },
            static () => { },
            TimeProvider.System,
            default);

        return new SortedDictionary<string, string>(found, StringComparer.Ordinal);

        static string Line(string kind, WalkEntry entry) =>
            $"{kind} {entry.FullName} {entry.Attributes} {entry.Length} {entry.CreationTimeUtc.Ticks} {entry.LastWriteTimeUtc.Ticks}";
    }

    /// <summary>How many progress reports one walk of <paramref name="root"/> makes, on one worker.</summary>
    private static int Reports(string root, ManualTimeProvider clock, TimeSpan advanceBy)
    {
        var reports = 0;

        BoundedFileWalk.Visit<byte>(
            root,
            0,
            new WalkTuning(1, WalkTuning.MinimumListingBuffer),
            (_, contents, descend) =>
            {
                foreach (var entry in contents.Entries.Where(e => e.IsDirectory))
                {
                    descend(entry, 0);
                }
            },
            () =>
            {
                reports++;
                clock.Advance(advanceBy);
            },
            clock,
            default);

        return reports;
    }

    /// <summary>
    /// Every entry the state-carrying walk hands back, by the path it was handed back under.
    /// </summary>
    private static List<string> Reached(string root)
    {
        var seen = new ConcurrentBag<string>();

        Walk<byte>(root, 0, (_, contents, descend) =>
        {
            foreach (var entry in contents.Entries)
            {
                seen.Add(entry.FullName);

                if (entry.IsDirectory)
                {
                    descend(entry, 0);
                }
            }
        });

        Assert.Equal(4, seen.Count);
        return [.. seen];
    }

    private static List<string> Visited(string root)
    {
        var seen = new ConcurrentBag<string>();

        BoundedFileWalk.Visit(
            root, WalkTuning.Default, file => seen.Add(file.FullName), _ => { }, () => { }, TimeProvider.System, default);

        Assert.Equal(2, seen.Count);
        return [.. seen];
    }

    private static void Walk<TState>(
        string root,
        TState rootState,
        Action<TState, DirectoryContents, Action<WalkEntry, TState>> onDirectory,
        WalkTuning? tuning = null) =>
        BoundedFileWalk.Visit(root, rootState, tuning ?? WalkTuning.Default, onDirectory, static () => { }, TimeProvider.System, default);
}
