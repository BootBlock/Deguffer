using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Tests;

/// <summary>
/// The checksums a search read are kept from one search to the next under the file's volume, number,
/// length and last-modified and change times, never its path, so a second search reads only what
/// changed (§7.4); a volume whose numbers do not stay with their files keeps nothing, and a store
/// that cannot be read loads empty.
/// </summary>
public sealed class ChecksumCacheTests : IDisposable
{
    private const int Block = ContentReader.BlockBytes;

    private readonly DuplicateTree _tree = new();
    private readonly ManualTimeProvider _clock = new();
    /// <summary>How many bytes of content were read into a checksum, across every read.</summary>
    private long _read;

    public void Dispose() => _tree.Dispose();

    private string Store => ChecksumCache.FileOf(_tree.Environment);

    /// <summary>
    /// The first search reads every file; a second over the same unchanged files, by a cache that
    /// has only the store to go on, as the next launch of the app would, reads no byte of any of
    /// them, and finds the same groups with the same checksums.
    /// </summary>
    [Fact]
    public async Task ASecondSearchOverAnUnchangedTreeReadsNoFile()
    {
        WriteTree();

        var first = await SearchAsync(new ChecksumCache(Store, _clock));
        var readFirst = _read;
        _read = 0;
        var second = await SearchAsync(new ChecksumCache(Store, _clock));

        Assert.True(readFirst > 6 * Block, $"The first search read {readFirst} bytes.");
        Assert.Equal(0, _read);
        Assert.Equal(2, first.Groups.Count);
        Assert.Equal(Described(first), Described(second));
    }

    /// <summary>
    /// The first and last blocks of a file larger than the two are read by XXH128 whatever the
    /// search's checksum, so their value is kept under XXH128 and serves a search by any checksum. A
    /// search by SHA-512 after one by SHA-256 reads no larger file's ends, only the full content of
    /// the files whose ends matched and the small files, whose value is the group's by SHA-512.
    /// </summary>
    [Fact]
    public async Task TheEndsOfALargerFileAreKeptOnceForEveryChecksum()
    {
        var (a, _) = WriteTree();
        var cache = new ChecksumCache(Store, _clock);

        await SearchAsync(cache, ChecksumAlgorithm.Sha256);
        _read = 0;
        var second = await SearchAsync(cache, ChecksumAlgorithm.Sha512);

        Assert.NotNull(cache.Find(Describe(a), ContentPart.Ends, ChecksumAlgorithm.XxHash128));
        Assert.Null(cache.Find(Describe(a), ContentPart.Ends, ChecksumAlgorithm.Sha256));
        Assert.Equal((3 * 3 * Block) + (2 * 5000), _read);
        Assert.All(second.Groups, group => Assert.Equal(ChecksumAlgorithm.Sha512, group.Checksum!.Value.Algorithm));
        Assert.Equal(2, second.Groups.Count);
    }

    /// <summary>
    /// A file whose bytes were changed and whose last-modified time was then put back, as a program
    /// that restores times does, has the same length and last-modified time as before: its change
    /// time is what moved, so its old checksum is not used and it no longer matches its copy.
    /// </summary>
    [Fact]
    public async Task AFileRewrittenWithItsOldTimePutBackIsReadAgain()
    {
        var (a, _) = WriteTree();
        await SearchAsync(new ChecksumCache(Store, _clock));
        var modified = File.GetLastWriteTimeUtc(a);
        var before = Describe(a);

        UntilTheChangeTimeMoves(a, before.Changed, () =>
        {
            File.WriteAllBytes(a, Random(3 * Block, 99));
            File.SetLastWriteTimeUtc(a, modified);
        });

        var after = Describe(a);
        Assert.Equal((before.Identity, before.Length, before.Modified), (after.Identity, after.Length, after.Modified));
        Assert.NotEqual(before.Changed, after.Changed);

        var second = await SearchAsync(new ChecksumCache(Store, _clock));

        Assert.DoesNotContain(second.Groups, group => group.Files.Any(file => file.Name == "a.bin"));
    }

    /// <summary>
    /// The key is every field it names: a description differing from the remembered one in any of
    /// them, or a different algorithm or part, finds nothing, and the same description finds the
    /// value.
    /// </summary>
    [Theory]
    [InlineData("volume")]
    [InlineData("file")]
    [InlineData("length")]
    [InlineData("modified")]
    [InlineData("changed")]
    [InlineData("algorithm")]
    [InlineData("part")]
    public void AChangeToAnyKeyFieldMissesTheCache(string field)
    {
        var cache = new ChecksumCache(Store, _clock);
        var remembered = State();
        var value = Value(ChecksumAlgorithm.XxHash128, 1);
        cache.Remember(remembered, ContentPart.Whole, value);

        var (asked, part, algorithm) = field switch
        {
            "volume" => (remembered with { Identity = remembered.Identity with { Volume = 2 } }, ContentPart.Whole, ChecksumAlgorithm.XxHash128),
            "file" => (remembered with { Identity = remembered.Identity with { File = 8 } }, ContentPart.Whole, ChecksumAlgorithm.XxHash128),
            "length" => (remembered with { Length = remembered.Length + 1 }, ContentPart.Whole, ChecksumAlgorithm.XxHash128),
            "modified" => (remembered with { Modified = remembered.Modified.AddTicks(1) }, ContentPart.Whole, ChecksumAlgorithm.XxHash128),
            "changed" => (remembered with { Changed = remembered.Changed.AddTicks(1) }, ContentPart.Whole, ChecksumAlgorithm.XxHash128),
            "algorithm" => (remembered, ContentPart.Whole, ChecksumAlgorithm.Sha256),
            _ => (remembered, ContentPart.Ends, ChecksumAlgorithm.XxHash128),
        };

        Assert.Equal(value, cache.Find(remembered, ContentPart.Whole, ChecksumAlgorithm.XxHash128));
        Assert.Null(cache.Find(asked, part, algorithm));
    }

    /// <summary>
    /// The store names no file: after a search over files with distinctive names, neither a name nor
    /// a path is in it in any encoding Windows or .NET would write one in, while it does hold values.
    /// </summary>
    [Fact]
    public async Task TheStoreHoldsNoPath()
    {
        var content = Random(5000, 7);
        var named = _tree.File(content, "Data", "Ünïque-photo-297.jpg");
        _tree.File(content, "Data", "Other", "Ünïque-photo-297.jpg");

        await SearchAsync(new ChecksumCache(Store, _clock));

        var stored = File.ReadAllBytes(Store);
        Assert.True(stored.Length > 100, "The store holds no values, so it proves nothing about paths.");

        foreach (var text in new[] { "Ünïque-photo-297", named, LongPath.Extended(named), _tree.Top })
        {
            foreach (var encoding in new Encoding[] { Encoding.Unicode, Encoding.UTF8, Encoding.Latin1 })
            {
                Assert.True(stored.AsSpan().IndexOf(encoding.GetBytes(text)) < 0, $"The store holds '{text}' as {encoding.WebName}.");
            }
        }
    }

    /// <summary>
    /// FAT and exFAT make a file's number up from where its entry lies, so another file can have it
    /// by the next search: nothing is kept for a volume that is not NTFS or ReFS, and one that would
    /// not name its file system keeps nothing either. The files are on NTFS, so only what the volume
    /// says decides it.
    /// </summary>
    [Theory]
    [InlineData("FAT32")]
    [InlineData("exFAT")]
    [InlineData("FAT")]
    [InlineData(null)]
    public async Task AVolumeThatIsNotNtfsOrRefsIsNeverCached(string? fileSystem)
    {
        using var tree = new DuplicateTree(fileSystem);
        var content = Random(5000, 8);
        tree.File(content, "Data", "a.bin");
        tree.File(content, "Data", "Other", "a.bin");
        var store = ChecksumCache.FileOf(tree.Environment);

        var first = await SearchAsync(tree, new ChecksumCache(store, _clock));
        var readFirst = _read;
        _read = 0;
        await SearchAsync(tree, new ChecksumCache(store, _clock));

        Assert.Single(first.Groups);
        Assert.Equal(readFirst, _read);
        Assert.False(File.Exists(store), "A store was written for a volume whose numbers do not stay with their files.");
    }

    /// <summary>
    /// Damaged in any way, the store loads as no store at all, never as part of one or with a value
    /// read wrongly, and the next save writes a whole one.
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("garbage")]
    [InlineData("truncated")]
    [InlineData("a value's byte flipped")]
    [InlineData("an unknown version")]
    [InlineData("an algorithm no value has")]
    public void ACorruptStoreLoadsEmpty(string damage)
    {
        var description = State();
        var value = Value(ChecksumAlgorithm.Sha256, 2);
        var cache = new ChecksumCache(Store, _clock);
        cache.Remember(description, ContentPart.Whole, value);
        Assert.True(cache.Save());

        var stored = File.ReadAllBytes(Store);
        File.WriteAllBytes(Store, damage switch
        {
            "empty" => [],
            "garbage" => Random(stored.Length, 3),
            "truncated" => stored[..^12],
            "a value's byte flipped" => Flipped(stored, stored.Length - 9),
            "an unknown version" => Resealed(stored, body => BinaryPrimitives.WriteInt32LittleEndian(body[4..], 2)),
            _ => Resealed(stored, body => body[12 + 48] = 200),
        });

        var reloaded = new ChecksumCache(Store, _clock);

        Assert.Null(ChecksumStoreFormat.Read(File.ReadAllBytes(Store)));
        Assert.Null(reloaded.Find(description, ContentPart.Whole, ChecksumAlgorithm.Sha256));

        reloaded.Remember(description, ContentPart.Whole, value);
        Assert.True(reloaded.Save());
        Assert.Equal(value, new ChecksumCache(Store, _clock).Find(description, ContentPart.Whole, ChecksumAlgorithm.Sha256));
    }

    /// <summary>
    /// A value no search has used for longer than the cache keeps one is dropped when the store is
    /// written, and one used since is kept; past the most it keeps, the values used longest ago go.
    /// </summary>
    [Fact]
    public void AValueUnusedTooLongOrPastTheBoundIsDropped()
    {
        var unused = State() with { Identity = new FileIdentity(1, 1) };
        var used = State() with { Identity = new FileIdentity(1, 2) };
        var cache = new ChecksumCache(Store, _clock);
        cache.Remember(unused, ContentPart.Whole, Value(ChecksumAlgorithm.XxHash128, 1));
        cache.Remember(used, ContentPart.Whole, Value(ChecksumAlgorithm.XxHash128, 2));

        _clock.Advance(TimeSpan.FromDays(100));
        Assert.NotNull(cache.Find(used, ContentPart.Whole, ChecksumAlgorithm.XxHash128));
        _clock.Advance(TimeSpan.FromDays(ChecksumCache.DaysKeptUnused - 99));
        Assert.True(cache.Save());

        var reloaded = new ChecksumCache(Store, _clock);
        Assert.Null(reloaded.Find(unused, ContentPart.Whole, ChecksumAlgorithm.XxHash128));
        Assert.NotNull(reloaded.Find(used, ContentPart.Whole, ChecksumAlgorithm.XxHash128));

        var bounded = new ChecksumCache(Store + ".bounded", _clock, mostKept: 2);
        var oldest = State() with { Identity = new FileIdentity(2, 1) };
        bounded.Remember(oldest, ContentPart.Whole, Value(ChecksumAlgorithm.XxHash128, 3));
        _clock.Advance(TimeSpan.FromDays(1));
        bounded.Remember(used, ContentPart.Whole, Value(ChecksumAlgorithm.XxHash128, 4));
        bounded.Remember(unused, ContentPart.Whole, Value(ChecksumAlgorithm.XxHash128, 5));
        Assert.True(bounded.Save());

        Assert.Null(bounded.Find(oldest, ContentPart.Whole, ChecksumAlgorithm.XxHash128));
        Assert.NotNull(bounded.Find(used, ContentPart.Whole, ChecksumAlgorithm.XxHash128));
        Assert.NotNull(bounded.Find(unused, ContentPart.Whole, ChecksumAlgorithm.XxHash128));
    }

    /// <summary>
    /// A file that went online-only after its checksum was kept is left out as a read would leave
    /// it, never answered from the cache: the remembered value is asked for only once the file is
    /// described now and judged.
    /// </summary>
    [Fact]
    public void AFileThatWentOnlineOnlyIsNotAnsweredFromTheCache()
    {
        var file = Candidate(_tree.File(Random(5000, 9), "Data", "a.bin"));
        var cache = new ChecksumCache(Store, _clock);
        Assert.Equal(ContentReadResult.Read, Read(Reader(cache), file).Result);

        var online = new ContentReader(
            FileInformation.Default,
            (path, route) => FileInformation.Default.Hold(path, route) is { Reading.State: { } state } held
                ? held with { Reading = held.Reading with { State = state with { Attributes = state.Attributes | (FileAttributes)0x0040_0000 } } }
                : throw new InvalidOperationException("The fixture's file could not be described."),
            (_, _, _) => throw new InvalidOperationException("The content of a file not on this device was opened."),
            NoPath,
            cache);

        Assert.Equal(ContentReadResult.OnlyInTheCloud, Read(online, file).Result);
    }

    /// <summary>
    /// A kept checksum stands in for reading the bytes, never for opening them: a file another
    /// program now holds without sharing it is left out as a failed read, as it would be with no
    /// cache, though nothing about it changed (§7.4, "A read that fails leaves the file out").
    /// </summary>
    [Fact]
    public void AFileAnotherProgramNowHoldsIsLeftOutThoughItsChecksumIsKept()
    {
        var path = _tree.File(Random(5000, 12), "Data", "a.bin");
        var file = Candidate(path);
        var cache = new ChecksumCache(Store, _clock);
        Assert.Equal(ContentReadResult.Read, Read(Reader(cache), file).Result);
        Assert.NotNull(cache.Find(Describe(path), ContentPart.Whole, ChecksumAlgorithm.XxHash128));

        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(ContentReadResult.ReadFailed, Read(Reader(cache), file).Result);
    }

    /// <summary>
    /// A file whose security was changed while it was read is read as before, since its bytes could
    /// not change, but its checksum is not kept: its change time moved, so the value would be kept
    /// under a key the file no longer has, or under one it does not yet have.
    /// </summary>
    [Fact]
    public void AFileChangedWhileItIsReadIsNotKept()
    {
        var path = _tree.File(Random(5000, 10), "Data", "a.bin");
        var file = Candidate(path);
        var before = Describe(path);
        var cache = new ChecksumCache(Store, _clock);
        var granted = false;
        using var checksum = new OnFirstAppend(() => UntilTheChangeTimeMoves(path, before.Changed, () => GrantEveryoneRead(path, granted = !granted)));

        var reading = Reader(cache).Read(file, ContentPart.Whole, checksum, default);

        Assert.NotEqual(before.Changed, Describe(path).Changed);
        Assert.Equal(ContentReadResult.Read, reading.Result);
        Assert.Null(cache.Find(before, ContentPart.Whole, ChecksumAlgorithm.XxHash128));
        Assert.Null(cache.Find(Describe(path), ContentPart.Whole, ChecksumAlgorithm.XxHash128));
    }

    /// <summary>
    /// The searcher the page builds, from the app's one cache, reads through that cache and starts
    /// storing what it read as the search ends, so the next search, or the next launch, can use it.
    /// </summary>
    [Fact]
    public async Task ThePagesSearcherReadsThroughTheCacheAndStoresWhatItRead()
    {
        var (a, _) = WriteTree();
        var cache = new ChecksumCache(Store, _clock);
        var searcher = new DuplicateSearcher(_tree.Finder(), new VolumeMediaCache(new FakeStorageQueries()), cache);

        await searcher.SearchAsync(new DuplicateSearch(MatchCriteria.Content, [new SearchLocation(Path.Combine(_tree.Top, "Data"))]), _tree.Policy());
        await cache.WhenStoredAsync(default);

        var stored = new ChecksumCache(Store, _clock);
        Assert.NotNull(stored.Find(Describe(a), ContentPart.Ends, ChecksumAlgorithm.XxHash128));
        Assert.NotNull(stored.Find(Describe(a), ContentPart.Whole, ChecksumAlgorithm.XxHash128));
    }

    /// <summary>
    /// A file identified by the older call carries the volume's serial number at half its width, so
    /// its key could name another volume's file: nothing is kept for it, even on NTFS.
    /// </summary>
    [Fact]
    public void AFileIdentifiedByTheOlderCallIsNeverCached()
    {
        var path = _tree.File(Random(5000, 11), "Data", "a.bin");
        var legacy = FileInformation.Default.Describe(path, IdentityRoute.Legacy).Description!;
        var file = Candidate(path) with { Identity = legacy.Identity, Route = IdentityRoute.Legacy };
        var cache = new ChecksumCache(Store, _clock);

        Assert.Equal(ContentReadResult.Read, Read(Reader(cache), file).Result);
        Assert.Equal(ContentReadResult.Read, Read(Reader(cache), file).Result);

        Assert.Equal(2 * 5000, _read);
        Assert.Null(cache.Find(legacy, ContentPart.Whole, ChecksumAlgorithm.XxHash128));
    }

    /// <summary>
    /// A search stopped while it reads content keeps what it had read by then: each value is as true
    /// of its file as it was, so the store is written as it is when a search ends.
    /// </summary>
    [Fact]
    public async Task AStoppedSearchStoresWhatItHadRead()
    {
        for (var i = 0; i < 6; i++)
        {
            var content = Random(5000 + i, 20 + i);
            _tree.File(content, "Data", $"{i}.bin");
            _tree.File(content, "Data", "Other", $"{i}.bin");
        }

        var cache = new ChecksumCache(Store, _clock);
        using var stop = new CancellationTokenSource();
        List<string> read = [];

        var result = await _tree.Searcher(
            (file, part, checksum, ct) =>
            {
                var reading = Reader(cache).Read(file, part, checksum, ct);

                lock (read)
                {
                    read.Add(file.Path);

                    if (read.Count == 1)
                    {
                        stop.Cancel();
                    }
                }

                return reading;
            },
            cache).SearchAsync(new DuplicateSearch(MatchCriteria.Content, [new SearchLocation(Path.Combine(_tree.Top, "Data"))]), _tree.Policy(), ct: stop.Token);
        await cache.WhenStoredAsync(default);

        Assert.True(result.Stopped);
        Assert.NotNull(new ChecksumCache(Store, _clock).Find(Describe(read[0]), ContentPart.Ends, ChecksumAlgorithm.XxHash128));
    }

    /// <summary>
    /// A search answers while the store it started writing is still being written, so a full store's
    /// write never stands between the last group confirmed and the result shown; the write still
    /// lands once it ends.
    /// </summary>
    [Fact]
    public async Task ASearchAnswersWhileItsStoreIsStillBeingWritten()
    {
        var (a, _) = WriteTree();
        using var store = new HeldStore(held: true);
        var cache = new ChecksumCache(Store, _clock, store: store.Write);

        var result = await AnswerAsync(_tree, cache, Reader(cache).Read).WaitAsync(TimeSpan.FromSeconds(10));
        await store.Started.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, result.Groups.Count);
        Assert.False(File.Exists(Store), "The store was written before the held write was released.");

        store.Release();
        await cache.WhenStoredAsync(default);

        Assert.NotNull(new ChecksumCache(Store, _clock).Find(Describe(a), ContentPart.Whole, ChecksumAlgorithm.XxHash128));
    }

    /// <summary>
    /// A search started while the last one's store is being written reads nothing until that write
    /// ends, and then uses every value the last search read; no two writes ever run at once.
    /// </summary>
    [Fact]
    public async Task ASearchStartedWhileTheStoreIsWrittenWaitsForItAndUsesEveryValue()
    {
        WriteTree();
        using var store = new HeldStore(held: true);
        var cache = new ChecksumCache(Store, _clock, store: store.Write);
        var first = await AnswerAsync(_tree, cache, Reader(cache).Read).WaitAsync(TimeSpan.FromSeconds(10));
        await store.Started.WaitAsync(TimeSpan.FromSeconds(10));

        _read = 0;
        var readWhileHeld = false;
        var reachedContent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counted = Counted(Reader(cache));
        var searching = _tree.Searcher(
            (file, part, checksum, ct) =>
            {
                readWhileHeld |= !store.IsReleased;

                return counted(file, part, checksum, ct);
            },
            cache).SearchAsync(
                new DuplicateSearch(MatchCriteria.Content, [new SearchLocation(Path.Combine(_tree.Top, "Data"))]),
                _tree.Policy(),
                candidatesFound: (_, _) =>
                {
                    reachedContent.TrySetResult();
                    return Task.CompletedTask;
                });

        // Long enough for the second search's reads to start, were it not waiting for the write.
        await reachedContent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.False(searching.IsCompleted, "The second search answered while the last one's store was still being written.");

        store.Release();
        var second = await searching.WaitAsync(TimeSpan.FromSeconds(10));
        await cache.WhenStoredAsync(default);

        Assert.False(readWhileHeld, "The second search read a file while the last one's store was being written.");
        Assert.Equal(0, _read);
        Assert.Equal(Described(first), Described(second));
        Assert.Equal(1, store.Writes);
        Assert.False(store.Overlapped, "Two writes of the store ran at once.");
    }

    /// <summary>
    /// A search that read nothing new, only using remembered values on a new day, does not write the
    /// store again; the next search with a new value writes those days with it, and a save, as the
    /// app's close makes, writes days that no search has.
    /// </summary>
    [Fact]
    public async Task NewLastUsedDaysAloneWaitForTheNextNewValueOrTheClose()
    {
        WriteTree();
        using var store = new HeldStore(held: false);
        var cache = new ChecksumCache(Store, _clock, store: store.Write);
        await SearchAsync(cache);
        var firstDay = Today;
        Assert.Equal(1, store.Writes);

        _clock.Advance(TimeSpan.FromDays(1));
        _read = 0;
        await SearchAsync(cache);

        Assert.Equal(0, _read);
        Assert.Equal(1, store.Writes);
        Assert.All(StoredDays(), day => Assert.Equal(firstDay, day));

        var content = Random(6000, 30);
        _tree.File(content, "Data", "new.bin");
        _tree.File(content, "Data", "Other", "new.bin");
        await SearchAsync(cache);

        Assert.Equal(2, store.Writes);
        Assert.All(StoredDays(), day => Assert.Equal(Today, day));

        _clock.Advance(TimeSpan.FromDays(1));
        await SearchAsync(cache);
        Assert.Equal(2, store.Writes);

        Assert.True(cache.Save());

        Assert.Equal(3, store.Writes);
        Assert.All(StoredDays(), day => Assert.Equal(Today, day));
    }

    /// <summary>
    /// A write the disk refuses answers false and leaves the cache marked changed, so the next save
    /// writes what the refused one would have.
    /// </summary>
    [Fact]
    public void AWriteThatFailsIsMadeAgainByTheNextSave()
    {
        var description = State();
        var value = Value(ChecksumAlgorithm.XxHash128, 6);
        var refusals = 1;
        var cache = new ChecksumCache(Store, _clock, store: (file, write) =>
        {
            if (refusals-- > 0)
            {
                throw new IOException("The disk is full.");
            }

            StoreFile.Replace(file, write);
        });
        cache.Remember(description, ContentPart.Whole, value);

        Assert.False(cache.Save());
        Assert.False(File.Exists(Store));

        Assert.True(cache.Save());
        Assert.Equal(value, new ChecksumCache(Store, _clock).Find(description, ContentPart.Whole, ChecksumAlgorithm.XxHash128));
    }

    /// <summary>
    /// A write that fails in a way no write expects reaches whoever waits on it, the save that
    /// started it and the next search, and no one after them: that search's end writes the store
    /// again though it read nothing new, and every wait and save after it succeeds. The write that
    /// faults is one only a new last-used day started, as the close's can be.
    /// </summary>
    [Fact]
    public async Task AWriteThatFaultsReachesTheNextSearchAndNoOneAfterIt()
    {
        var description = State();
        var value = Value(ChecksumAlgorithm.XxHash128, 7);
        var writes = 0;
        var cache = new ChecksumCache(Store, _clock, store: (file, write) =>
        {
            if (++writes == 2)
            {
                throw new InvalidOperationException("A write no store write expects.");
            }

            StoreFile.Replace(file, write);
        });
        cache.Remember(description, ContentPart.Whole, value);
        Assert.True(cache.Save());

        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(value, cache.Find(description, ContentPart.Whole, ChecksumAlgorithm.XxHash128));

        Assert.Throws<InvalidOperationException>(() => cache.Save());
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.WhenStoredAsync(default));

        cache.StoreInBackground();
        await cache.WhenStoredAsync(default);

        Assert.Equal(3, writes);
        Assert.All(StoredDays(), day => Assert.Equal(Today, day));
        Assert.True(cache.Save());
        await cache.WhenStoredAsync(default);
    }

    /// <summary>
    /// Two copies of three blocks, a file of their length differing only in its middle, and two
    /// small copies read whole by their ends: every stage of a content search has something to read.
    /// </summary>
    private (string A, string Copy) WriteTree()
    {
        var large = Random(3 * Block, 1);
        var middle = (byte[])large.Clone();
        middle[Block + 7] ^= 0xFF;
        var small = Random(5000, 2);

        var a = _tree.File(large, "Data", "a.bin");
        var copy = _tree.File(large, "Data", "Other", "copy.bin");
        _tree.File(middle, "Data", "middle.bin");
        _tree.File(small, "Data", "small.bin");
        _tree.File(small, "Data", "Other", "small.bin");

        return (a, copy);
    }

    private Task<DuplicateSearchResult> SearchAsync(ChecksumCache cache, ChecksumAlgorithm algorithm = ChecksumAlgorithm.XxHash128) =>
        SearchAsync(_tree, cache, algorithm);

    /// <summary>A content search through <paramref name="cache"/>, once the store write it started has ended.</summary>
    private async Task<DuplicateSearchResult> SearchAsync(
        DuplicateTree tree, ChecksumCache cache, ChecksumAlgorithm algorithm = ChecksumAlgorithm.XxHash128)
    {
        var result = await AnswerAsync(tree, cache, Counted(Reader(cache)), algorithm);
        await cache.WhenStoredAsync(default);

        return result;
    }

    /// <summary>A content search through <paramref name="cache"/>, as soon as it answers.</summary>
    private static Task<DuplicateSearchResult> AnswerAsync(
        DuplicateTree tree, ChecksumCache cache, ReadContent read, ChecksumAlgorithm algorithm = ChecksumAlgorithm.XxHash128) =>
        tree.Searcher(read, cache).SearchAsync(
            new DuplicateSearch(MatchCriteria.Content, [new SearchLocation(Path.Combine(tree.Top, "Data"))], algorithm), tree.Policy());

    /// <summary>Every last-used day the store holds.</summary>
    private IReadOnlyList<int> StoredDays() =>
        [.. ChecksumStoreFormat.Read(File.ReadAllBytes(Store))!.Values.Select(value => value.LastUsedDay)];

    private int Today => (int)(_clock.GetUtcNow().UtcDateTime - DateTime.UnixEpoch).TotalDays;

    /// <summary>A reader using <paramref name="cache"/>.</summary>
    private static ContentReader Reader(ChecksumCache cache) =>
        new(FileInformation.Default, FileInformation.Default.Hold, FileInformation.OpenById, NoPath, cache);

    /// <summary><paramref name="reader"/>, counting the bytes each read puts into its checksum.</summary>
    private ReadContent Counted(ContentReader reader) => (file, part, checksum, ct) =>
    {
        using var counted = new Counting(checksum, bytes => Interlocked.Add(ref _read, bytes));

        return reader.Read(file, part, counted, ct);
    };

    private static IReadOnlyList<string> Described(DuplicateSearchResult result) =>
        [.. result.Groups
            .Select(group => $"{group.Checksum}: {string.Join(", ", group.Files.Select(file => file.Path).Order(StringComparer.Ordinal))}")
            .Order(StringComparer.Ordinal)];

    private static FileDescription Describe(string path) => FileInformation.Default.Describe(path, IdentityRoute.FileId).Description!;

    /// <summary>
    /// Makes <paramref name="change"/> to the file at least once, and again until its change time is
    /// no longer <paramref name="changed"/>. NTFS stamps a change with the system time as of the last
    /// clock interrupt, so one made within the tick that stamped the file's last change leaves the
    /// change time where it was, as most made straight after it do. Each call must be a real change.
    /// The limit only keeps a broken premise from hanging the test.
    /// </summary>
    private static void UntilTheChangeTimeMoves(string path, DateTime changed, Action change)
    {
        var limit = Stopwatch.StartNew();

        do
        {
            change();
        }
        while (Describe(path).Changed == changed && limit.Elapsed < TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Grants everyone the right to read the file, or withdraws it: called in turn, each call undoes
    /// the last, so every call is a real change.
    /// </summary>
    private static void GrantEveryoneRead(string path, bool grant)
    {
        var everyone = new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow);
        var info = new FileInfo(path);
        var security = info.GetAccessControl();

        if (grant)
        {
            security.AddAccessRule(everyone);
        }
        else
        {
            security.RemoveAccessRule(everyone);
        }

        info.SetAccessControl(security);
    }

    /// <summary>The file as a search on an NTFS volume identified it.</summary>
    private static DuplicateCandidate Candidate(string path)
    {
        var file = ContentReaderTests.Candidate(path);

        return file with { Volume = file.Volume with { FileSystem = "NTFS" } };
    }

    private ContentReading Read(ContentReader reader, DuplicateCandidate file)
    {
        using var checksum = Checksum.For(ChecksumAlgorithm.XxHash128);

        return Counted(reader)(file, ContentPart.Whole, checksum, default);
    }

    private static FileState State() => new(
        new FileIdentity(1, 7), 3 * Block, 3 * Block, 1, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc), FileAttributes.Archive, ReparseTag: 0);

    private static ContentChecksum Value(ChecksumAlgorithm algorithm, int seed) =>
        new(algorithm, Random(ChecksumAlgorithms.DigestBytes(algorithm)!.Value, seed));

    private static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Flipped(byte[] stored, int at)
    {
        var damaged = (byte[])stored.Clone();
        damaged[at] ^= 0xFF;
        return damaged;
    }

    /// <summary>The store changed by <paramref name="change"/>, with its trailing XXH64 written again, so only the change can refuse it.</summary>
    private static byte[] Resealed(byte[] stored, SpanAction change)
    {
        var damaged = (byte[])stored.Clone();
        var body = damaged.AsSpan(0, damaged.Length - sizeof(ulong));
        change(body);
        BinaryPrimitives.WriteUInt64LittleEndian(damaged.AsSpan(body.Length), XxHash64.HashToUInt64(body));
        return damaged;
    }

    private delegate void SpanAction(Span<byte> body);

    private static SafeFileHandle NoPath(string extended) =>
        throw new InvalidOperationException("The content was opened by its path on a volume that can hold links.");

    /// <summary>
    /// A running checksum that tells <paramref name="read"/> how many bytes each append carries, and
    /// otherwise is <paramref name="inner"/>, which the search owns and disposes.
    /// </summary>
    private sealed class Counting(Checksum inner, Action<int> read) : Checksum(inner.Algorithm)
    {
        public override void Append(ReadOnlySpan<byte> data)
        {
            read(data.Length);
            inner.Append(data);
        }

        public override ContentChecksum Finish() => inner.Finish();
    }

    /// <summary>
    /// Writes the store as the app does, but only once released where it starts held, counting the
    /// writes and noting any two that run at once.
    /// </summary>
    private sealed class HeldStore(bool held) : IDisposable
    {
        private readonly ManualResetEventSlim _released = new(!held);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;
        private int _writes;

        /// <summary>Completes as the first write starts.</summary>
        public Task Started => _started.Task;

        public bool IsReleased => _released.IsSet;

        public int Writes => Volatile.Read(ref _writes);

        public bool Overlapped { get; private set; }

        public void Release() => _released.Set();

        public void Write(string file, Action<Stream> write)
        {
            Overlapped |= Interlocked.Increment(ref _running) > 1;
            Interlocked.Increment(ref _writes);
            _started.TrySetResult();

            try
            {
                // Past any wait a test makes for the answer, so a search that waits for the write fails
                // that wait rather than hanging.
                if (!_released.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException("The held store write was never released.");
                }

                StoreFile.Replace(file, write);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public void Dispose()
        {
            _released.Set();
            _released.Dispose();
        }
    }

    /// <summary>An XXH128 checksum that runs <paramref name="first"/> as the first bytes arrive.</summary>
    private sealed class OnFirstAppend(Action first) : Checksum(ChecksumAlgorithm.XxHash128)
    {
        private readonly Checksum _inner = For(ChecksumAlgorithm.XxHash128);
        private bool _started;

        public override void Append(ReadOnlySpan<byte> data)
        {
            if (!_started)
            {
                _started = true;
                first();
            }

            _inner.Append(data);
        }

        public override ContentChecksum Finish() => _inner.Finish();

        public override void Dispose()
        {
            _inner.Dispose();
            base.Dispose();
        }
    }
}
