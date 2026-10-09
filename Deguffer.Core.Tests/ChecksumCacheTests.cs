using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Safety;
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
    private int _opened;

    public void Dispose() => _tree.Dispose();

    private string Store => ChecksumCache.FileOf(_tree.Environment);

    /// <summary>
    /// The first search reads every file; a second over the same unchanged files, by a cache that
    /// has only the store to go on, as the next launch of the app would, opens none for its content,
    /// and finds the same groups with the same checksums.
    /// </summary>
    [Fact]
    public async Task ASecondSearchOverAnUnchangedTreeReadsNoFile()
    {
        WriteTree();

        var first = await SearchAsync(new ChecksumCache(Store, _clock));
        var openedFirst = _opened;
        _opened = 0;
        var second = await SearchAsync(new ChecksumCache(Store, _clock));

        Assert.True(openedFirst >= 5, $"The first search opened {openedFirst} files.");
        Assert.Equal(0, _opened);
        Assert.Equal(2, first.Groups.Count);
        Assert.Equal(Described(first), Described(second));
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

        File.WriteAllBytes(a, Random(3 * Block, 99));
        File.SetLastWriteTimeUtc(a, modified);

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
        var remembered = Description();
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
        var openedFirst = _opened;
        _opened = 0;
        await SearchAsync(tree, new ChecksumCache(store, _clock));

        Assert.Single(first.Groups);
        Assert.Equal(openedFirst, _opened);
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
        var description = Description();
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
        var unused = Description() with { Identity = new FileIdentity(1, 1) };
        var used = Description() with { Identity = new FileIdentity(1, 2) };
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
        var oldest = Description() with { Identity = new FileIdentity(2, 1) };
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
            (path, route) => FileInformation.Default.Hold(path, route) is { Reading.Description: { } description } held
                ? held with { Reading = held.Reading with { Description = description with { Attributes = description.Attributes | (FileAttributes)0x0040_0000 } } }
                : throw new InvalidOperationException("The fixture's file could not be described."),
            (_, _, _) => throw new InvalidOperationException("The content of a file not on this device was opened."),
            NoPath,
            cache);

        Assert.Equal(ContentReadResult.OnlyInTheCloud, Read(online, file).Result);
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
        using var checksum = new OnFirstAppend(() =>
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            info.SetAccessControl(security);
        });

        var reading = Reader(cache).Read(file, ContentPart.Whole, checksum, default);

        Assert.NotEqual(before.Changed, Describe(path).Changed);
        Assert.Equal(ContentReadResult.Read, reading.Result);
        Assert.Null(cache.Find(before, ContentPart.Whole, ChecksumAlgorithm.XxHash128));
        Assert.Null(cache.Find(Describe(path), ContentPart.Whole, ChecksumAlgorithm.XxHash128));
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

    private Task<DuplicateSearchResult> SearchAsync(ChecksumCache cache) => SearchAsync(_tree, cache);

    private Task<DuplicateSearchResult> SearchAsync(DuplicateTree tree, ChecksumCache cache) =>
        tree.Searcher(Reader(cache).Read, cache).SearchAsync(
            new DuplicateSearch(MatchCriteria.Content, [new SearchLocation(Path.Combine(tree.Top, "Data"))]), tree.Policy());

    /// <summary>A reader using <paramref name="cache"/> that counts each file it opens for its content.</summary>
    private ContentReader Reader(ChecksumCache cache) => new(
        FileInformation.Default,
        FileInformation.Default.Hold,
        (held, identity, route) =>
        {
            Interlocked.Increment(ref _opened);
            return FileInformation.OpenById(held, identity, route);
        },
        NoPath,
        cache);

    private static IReadOnlyList<string> Described(DuplicateSearchResult result) =>
        [.. result.Groups
            .Select(group => $"{group.Checksum}: {string.Join(", ", group.Files.Select(file => file.Path).Order(StringComparer.Ordinal))}")
            .Order(StringComparer.Ordinal)];

    private static FileDescription Describe(string path) => FileInformation.Default.Describe(path, IdentityRoute.FileId).Description!;

    /// <summary>The file as a search on an NTFS volume identified it.</summary>
    private static DuplicateCandidate Candidate(string path)
    {
        var file = ContentReaderTests.Candidate(path);

        return file with { Volume = file.Volume with { FileSystem = "NTFS" } };
    }

    private static ContentReading Read(ContentReader reader, DuplicateCandidate file)
    {
        using var checksum = Checksum.For(ChecksumAlgorithm.XxHash128);

        return reader.Read(file, ContentPart.Whole, checksum, default);
    }

    private static FileDescription Description() => new(
        new FileIdentity(1, 7), @"X:\Data\a.bin", 3 * Block, 3 * Block, 1, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
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
