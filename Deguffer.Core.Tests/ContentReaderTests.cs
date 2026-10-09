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
    /// <summary><c>ERROR_NOT_SUPPORTED</c>.</summary>
    private const int NotSupported = 50;

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
            (described, route) => FileInformation.Default.Hold(described, route) is { Reading.Description: { } description } held
                ? held with { Reading = held.Reading with { Description = description with { Attributes = description.Attributes | (FileAttributes)recall } } }
                : throw new InvalidOperationException("The fixture's file could not be described."),
            (_, _, _) => throw new InvalidOperationException("The content of a file not on this device was opened."),
            _ => throw new InvalidOperationException("The content of a file not on this device was opened by its path."));

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
            FileInformation.Default.Hold,
            (held, identity, route) =>
            {
                File.SetAttributes(path, FileAttributes.Offline);
                return FileInformation.OpenById(held, identity, route);
            },
            NoPath);
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
    /// §6.3, asserted by the form of the path that reaches Windows where the path is described: the
    /// content is then opened by the file's number, which walks no path at all. A deep path opens
    /// here with or without the prefix, so only the form shows it was given.
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
        var files = new FileInformation(
            (extended, use) =>
            {
                described.Add(extended);
                return FileInformation.Open(extended, use);
            },
            FileInformation.ReadIdentity);
        var reader = new ContentReader(files, files.Hold, FileInformation.OpenById, NoPath);

        Assert.Equal(ContentReadResult.Read, Read(reader, file).Result);
        Assert.Equal(LongPath.Extended(file.Path), Assert.Single(described));
        Assert.StartsWith(@"\\?\", described[0], StringComparison.Ordinal);
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

    /// <summary>
    /// The content is opened by the file's number, never by walking its path again. Between the
    /// description and the open, the file is renamed away and a symbolic link put at its name, to
    /// another file of the same length and time. An open by path would go through the link to that
    /// file; the open by number reads the file that was described.
    ///
    /// <para>The file's own name is what is replaced, because its folder cannot be: Windows refuses
    /// to rename a folder, or any folder above it, while a handle is open on a file inside, and the
    /// handle the file was described through is held until it has been read.</para>
    /// </summary>
    [Fact]
    public void AFileReplacedByALinkAfterTheDescriptionIsNotFollowed()
    {
        var original = new byte[100];
        new Random(1).NextBytes(original);
        var other = new byte[100];
        new Random(2).NextBytes(other);
        var path = Path.Combine(_temp.CreateDirectory("Data"), "x.bin");
        File.WriteAllBytes(path, original);
        var file = Candidate(path);
        var decoy = Path.Combine(_temp.CreateDirectory("Elsewhere"), "x.bin");
        File.WriteAllBytes(decoy, other);
        File.SetLastWriteTimeUtc(decoy, file.Modified);
        var replaced = false;
        var reader = new ContentReader(
            FileInformation.Default,
            FileInformation.Default.Hold,
            (held, identity, route) =>
            {
                File.Move(path, Path.Combine(_temp.Path, "Data", "x.moved"));
                SymbolicLink.ToFile(path, decoy);
                replaced = true;
                return FileInformation.OpenById(held, identity, route);
            },
            NoPath);
        using var checksum = Checksum.For(ChecksumAlgorithm.Sha256);

        var reading = reader.Read(file, ContentPart.Whole, checksum, default);

        Assert.True(replaced);
        Assert.Equal(other, File.ReadAllBytes(path));
        Assert.Equal(ContentReadResult.Read, reading.Result);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(original)), reading.Checksum.Hex);
    }

    /// <summary>
    /// Where Windows will not open a file by its number, the path is opened instead where the volume
    /// answered that it supports no reparse points and the path starts at its own drive letter, so
    /// no link can be on the way, and it is opened in its extended form (§6.3).
    /// </summary>
    [Fact]
    public void AVolumeWithNoLinksIsReadByPathWhereItWillNotOpenByNumber()
    {
        var path = _temp.CreateFile(100, "a.bin");
        var file = Candidate(path, VolumeFeatures.None);
        List<string> opened = [];
        var reader = new ContentReader(FileInformation.Default, FileInformation.Default.Hold, Unsupported, extended =>
        {
            opened.Add(extended);
            return File.OpenHandle(extended, FileMode.Open, FileAccess.Read, FileShare.Read);
        });

        Assert.Equal(ContentReadResult.Read, Read(reader, file).Result);
        Assert.Equal(LongPath.Extended(file.Path), Assert.Single(opened));
        Assert.StartsWith(@"\\?\", opened[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A volume that refused to say what it supports may be NTFS and hold links, so its refusal is
    /// never read as an answer of no reparse points: a file it will not open by number is a failed
    /// read, and its path is never opened.
    /// </summary>
    [Fact]
    public void AVolumeThatWouldNotSayWhatItSupportsIsNeverReadByPath()
    {
        var file = Candidate(_temp.CreateFile(100, "a.bin"), VolumeFeatures.None, answered: false);
        var reader = new ContentReader(FileInformation.Default, FileInformation.Default.Hold, Unsupported, NoPath);

        Assert.Equal(ContentReadResult.ReadFailed, Read(reader, file).Result);
    }

    /// <summary>
    /// A volume with no reparse points mounted only in a folder of another volume is reached through
    /// that volume's folders, any of which can be replaced by a junction, so its path is never
    /// opened either. The scratch folder stands for the folder it is mounted at.
    /// </summary>
    [Fact]
    public void AVolumeReachedThroughAFolderOfAnotherIsNeverReadByPath()
    {
        var file = Candidate(_temp.CreateFile(100, "a.bin"), VolumeFeatures.None, mountedAt: _temp.Path + Path.DirectorySeparatorChar);
        var reader = new ContentReader(FileInformation.Default, FileInformation.Default.Hold, Unsupported, NoPath);

        Assert.Equal(ContentReadResult.ReadFailed, Read(reader, file).Result);
    }

    /// <summary>
    /// On a volume that supports reparse points, a link can be on the way, so a file Windows will
    /// not open by its number is never opened by path: it is left out as a failed read, and never
    /// as gone, because it was there when it was described.
    /// </summary>
    [Fact]
    public void AVolumeThatCanHoldLinksIsNeverReadByPath()
    {
        var path = _temp.CreateFile(100, "a.bin");
        var file = Candidate(path);
        var reader = new ContentReader(FileInformation.Default, FileInformation.Default.Hold, Unsupported, NoPath);

        Assert.Equal(ContentReadResult.ReadFailed, Read(reader, file).Result);
    }

    /// <summary>The file as the search identified it, which is what every check compares against.</summary>
    /// <param name="features">What the file's volume says it supports: an NTFS volume's reparse points, unless a test says otherwise.</param>
    /// <param name="answered">Whether the volume answered what it supports, rather than refusing the question.</param>
    /// <param name="mountedAt">Where the volume is mounted, or the drive the file's path starts at where none is given.</param>
    internal static DuplicateCandidate Candidate(
        string path,
        VolumeFeatures features = VolumeFeatures.ReparsePoints,
        bool answered = true,
        string? mountedAt = null)
    {
        var description = FileInformation.Default.Describe(path, IdentityRoute.FileId).Description!;

        return new DuplicateCandidate(
            description.Identity,
            description.Path,
            Path.GetFileName(path),
            [description.Path],
            description.Names,
            description.Length,
            description.Allocated,
            description.Modified,
            StorageAttributes.Of(description.Attributes),
            LocationRole.Search)
        {
            Volume = new LocalVolume(
                mountedAt ?? Path.GetPathRoot(description.Path)!,
                DriveType.Fixed,
                VolumeReadiness.Ready,
                Features: features,
                FeaturesAnswered: answered),
        };
    }

    private static ContentReading Read(ContentReader reader, DuplicateCandidate file)
    {
        using var checksum = Checksum.For(ChecksumAlgorithm.XxHash128);

        return reader.Read(file, ContentPart.Whole, checksum, default);
    }

    /// <summary>A path open that fails the test, for a volume where the content must be opened by number.</summary>
    private static SafeFileHandle NoPath(string extended) =>
        throw new InvalidOperationException("The content was opened by its path on a volume that can hold links.");

    /// <summary>A by-number open Windows refuses, as a file system that does not support it would.</summary>
    private static SafeFileHandle Unsupported(SafeFileHandle volume, FileIdentity identity, IdentityRoute route)
    {
        System.Runtime.InteropServices.Marshal.SetLastPInvokeError(NotSupported);
        return new SafeFileHandle(-1, ownsHandle: false);
    }

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
