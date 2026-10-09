using System.Diagnostics;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Tests;

/// <summary>
/// A file's content is read only where it is on this device and is still the file the search
/// identified, and a read that fails leaves the file out in the right count (§7.4).
/// </summary>
public sealed class ContentReaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A cloud provider sets a recall attribute on a file whose content has to be fetched, and
    /// neither can be set on an ordinary file, so the description of a real file is handed back
    /// with one added: the file went online-only after the search identified it. Opening it for its
    /// content could download it, so it must never be opened.
    /// </summary>
    [Theory]
    [InlineData(0x0004_0000)] // FILE_ATTRIBUTE_RECALL_ON_OPEN
    [InlineData(0x0040_0000)] // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
    public void AFileThatWentOnlineOnlyIsNeverOpenedForItsContent(int recall)
    {
        var path = _temp.CreateFile(100, "a.bin");
        var file = Candidate(path);
        var reader = new ContentReader(
            FileInformation.Default,
            (described, route) => FileInformation.Default.Describe(described, route) is { Description: { } description } reading
                ? reading with { Description = description with { Attributes = description.Attributes | (FileAttributes)recall } }
                : throw new InvalidOperationException("The fixture's file could not be described."),
            _ => throw new InvalidOperationException("The content of a file not on this device was opened."));

        Assert.Equal(ContentReadResult.OnlyInTheCloud, Read(reader, file).Result);
    }

    /// <summary>
    /// The attributes are asked again through the content's own handle before the first byte: a file
    /// that went online-only between the description and the open is not read. <c>OFFLINE</c> is
    /// the one cloud attribute an ordinary file can be given, and it is set as the file is opened.
    /// </summary>
    [Fact]
    public void AFileThatWentOnlineOnlyAsItWasOpenedIsNotRead()
    {
        var path = _temp.CreateFile(100, "a.bin");
        var file = Candidate(path);
        var reader = new ContentReader(
            FileInformation.Default,
            FileInformation.Default.Describe,
            extended =>
            {
                File.SetAttributes(path, FileAttributes.Offline);
                return Open(extended);
            });
        using var checksum = new Watched(_ => throw new InvalidOperationException("A file not on this device was read."));

        Assert.Equal(ContentReadResult.OnlyInTheCloud, reader.Read(file, ContentPart.Whole, checksum, default).Result);
    }

    /// <summary>
    /// Sharing only reading keeps any other program from writing to the file while it is read, but
    /// not from changing its times, so the file is described again through the same handle once the
    /// last byte is read. Its last-modified time is moved here as the first bytes arrive.
    /// </summary>
    [Fact]
    public void AFileThatChangesWhileItIsReadIsLeftOutAsChanged()
    {
        var path = _temp.CreateFile(3 * ContentReader.BlockBytes, "a.bin");
        var file = Candidate(path);
        var moved = false;
        using var checksum = new Watched(_ =>
        {
            if (!moved)
            {
                moved = true;
                File.SetLastWriteTimeUtc(path, file.Modified.AddSeconds(1));
            }
        });

        var reading = ContentReader.Default.Read(file, ContentPart.Whole, checksum, default);

        Assert.True(moved);
        Assert.Equal(ContentReadResult.Changed, reading.Result);
    }

    /// <summary>A file replaced at its path after it was identified is another file, whatever its length and time.</summary>
    [Fact]
    public void AFileReplacedAtItsPathIsLeftOutAsChanged()
    {
        var path = _temp.CreateFile(100, "a.bin");
        var file = Candidate(path);
        File.Delete(path);
        File.WriteAllBytes(path, new byte[100]);
        File.SetLastWriteTimeUtc(path, file.Modified);

        Assert.Equal(ContentReadResult.Changed, Read(ContentReader.Default, file).Result);
    }

    /// <summary>Held by another program that shares nothing: still there, and counted as a failed read.</summary>
    [Fact]
    public void ALockedFileIsLeftOutAsAFailedRead()
    {
        var path = _temp.CreateFile(100, "a.bin");
        var file = Candidate(path);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(ContentReadResult.ReadFailed, Read(ContentReader.Default, file).Result);
    }

    /// <summary>An access rule refusing the file's data: described, so plainly there, and never counted as gone.</summary>
    [Fact]
    public void AFileWhoseDataIsRefusedIsLeftOutAsAFailedRead()
    {
        var path = _temp.CreateFile(100, "a.bin");
        var file = Candidate(path);
        using var denied = DeniedDirectory.WithUnreadableContent(path);

        Assert.Equal(ContentReadResult.ReadFailed, Read(ContentReader.Default, file).Result);
    }

    /// <summary>A file Windows will not even describe may still be there, so it is never counted as gone.</summary>
    [Fact]
    public void AFileWindowsWillNotDescribeIsLeftOutAsUnidentified()
    {
        var path = _temp.CreateFile(100, "Data", "a.bin");
        var file = Candidate(path);
        using var denied = DeniedDirectory.WithUnreadableFile(path);

        Assert.Equal(ContentReadResult.Unidentified, Read(ContentReader.Default, file).Result);
    }

    [Fact]
    public void AFileDeletedAfterItWasIdentifiedIsGone()
    {
        var path = _temp.CreateFile(100, "a.bin");
        var file = Candidate(path);
        File.Delete(path);

        Assert.Equal(ContentReadResult.Gone, Read(ContentReader.Default, file).Result);
    }

    /// <summary>
    /// §6.3, asserted by the form of the path that reaches Windows, both where the path is described
    /// and where its content is opened. A deep path opens here with or without the prefix, so only
    /// the form shows it was given.
    /// </summary>
    [Fact]
    public void ThePathReachesWindowsInItsExtendedForm()
    {
        var deep = _temp.Path;

        while (deep.Length <= 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        var path = Path.Combine(deep, "a.bin");
        File.WriteAllBytes(LongPath.Extended(path), [1, 2, 3]);
        var file = Candidate(path);
        List<string> described = [];
        List<string> opened = [];
        var files = new FileInformation(
            (extended, use) =>
            {
                described.Add(extended);
                return FileInformation.Open(extended, use);
            },
            FileInformation.ReadIdentity);
        var reader = new ContentReader(files, files.Describe, extended =>
        {
            opened.Add(extended);
            return Open(extended);
        });

        Assert.Equal(ContentReadResult.Read, Read(reader, file).Result);
        Assert.Equal(LongPath.Extended(file.Path), Assert.Single(described));
        Assert.Equal(LongPath.Extended(file.Path), Assert.Single(opened));
        Assert.StartsWith(@"\\?\", opened[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A file no longer than its first and last blocks is read whole at once, and that reading is the
    /// file's full checksum, carried through without a second read.
    /// </summary>
    [Fact]
    public void AFileNoLongerThanTwoBlocksIsReadWholeByItsEnds()
    {
        var content = new byte[2 * ContentReader.BlockBytes];
        new Random(297).NextBytes(content);
        var path = Path.Combine(_temp.Path, "a.bin");
        File.WriteAllBytes(path, content);
        var file = Candidate(path);
        using var checksum = Checksum.For(ChecksumAlgorithm.Sha256);

        var ends = ContentReader.Default.Read(file, ContentPart.Ends, checksum, default);
        var whole = ContentReader.Default.Read(file, ContentPart.Whole, checksum, default);

        Assert.True(ContentReader.IsWhole(ContentPart.Ends, file.Length));
        Assert.Equal(whole.Checksum, ends.Checksum);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), ends.Checksum.Hex);
    }

    /// <summary>
    /// What Deguffer sees of a real online-only placeholder, as a process that is not the sync app:
    /// Windows disguises the placeholder by default, and the description through an attributes-only
    /// handle must still show that its content is not here, or the check before a read sees nothing.
    /// It does: disguised, the placeholder shows no reparse point and still carries
    /// <c>RECALL_ON_DATA_ACCESS</c>, so the check needs no placeholder mode set on its thread.
    /// </summary>
    [Fact]
    public void ARealOnlineOnlyPlaceholderIsDescribedAsNotOnThisDevice()
    {
        using var synced = new ScratchSyncRoot(_temp.CreateDirectory("Synced"));
        var online = synced.OnlineOnly("film.mkv", 5_000_000);
        synced.Disconnect();

        var description = FileInformation.Default.Describe(online, IdentityRoute.FileId).Description!;

        Assert.False(description.Attributes.HasFlag(FileAttributes.ReparsePoint), "The placeholder was not disguised, so this is not the view Deguffer has.");
        Assert.True((description.Attributes & (FileAttributes)0x0040_0000) != 0, "RECALL_ON_DATA_ACCESS was hidden.");
        Assert.Equal(FileStorage.CloudOnly, StorageAttributes.Of(description.Attributes));
        Assert.True(description.IsFile);
    }

    /// <summary>
    /// The same placeholder read for its content while the scratch root is connected, because only a
    /// connected sync app is asked for data, so its count of requests is the evidence. The read
    /// that follows proves the count is live.
    /// </summary>
    [Fact]
    public async Task ARealOnlineOnlyPlaceholderIsLeftOutAndNothingIsFetched()
    {
        using var synced = new ScratchSyncRoot(_temp.CreateDirectory("Synced"));
        var online = synced.OnlineOnly("film.mkv", 5_000_000);
        var file = Candidate(online);

        Assert.Equal(ContentReadResult.OnlyInTheCloud, Read(ContentReader.Default, file).Result);
        Assert.Equal(0, synced.FetchRequests);

        _ = Task.Run(() => File.ReadAllBytes(online));
        var clock = Stopwatch.StartNew();

        while (synced.FetchRequests == 0 && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50);
        }

        Assert.True(synced.FetchRequests > 0, "Reading the file's data asked the sync app for nothing.");
    }

    /// <summary>The file as the search identified it, which is what every check compares against.</summary>
    internal static DuplicateCandidate Candidate(string path)
    {
        var description = FileInformation.Default.Describe(path, IdentityRoute.FileId).Description!;

        return new DuplicateCandidate(
            description.Identity,
            description.Path,
            Path.GetFileName(path),
            [description.Path],
            description.Names,
            description.Length,
            description.Modified,
            StorageAttributes.Of(description.Attributes),
            LocationRole.Search);
    }

    private static ContentReading Read(ContentReader reader, DuplicateCandidate file)
    {
        using var checksum = Checksum.For(ChecksumAlgorithm.XxHash128);

        return reader.Read(file, ContentPart.Whole, checksum, default);
    }

    private static SafeFileHandle Open(string extended) =>
        File.OpenHandle(extended, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>A checksum that shows each piece appended to <paramref name="onAppend"/> first.</summary>
    private sealed class Watched(Action<int> onAppend) : Checksum(ChecksumAlgorithm.XxHash128)
    {
        private readonly Checksum _inner = For(ChecksumAlgorithm.XxHash128);

        public override void Append(ReadOnlySpan<byte> data)
        {
            onAppend(data.Length);
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
