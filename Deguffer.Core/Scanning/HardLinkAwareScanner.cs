using System.Runtime.InteropServices;
using Deguffer.Core.Safety;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Scanning;

/// <summary>
/// Measures the bytes a tree holds that nothing outside it also holds: the sum over files whose
/// hard-link count is exactly one. That is the number an eviction of the tree's contents can
/// actually give back, and for a store that hard-links into every consumer — pnpm's, conda's — it
/// is the only defensible figure. Summing file lengths there counts each linked copy, and the
/// reported reclaim would be several times what the disk returns, which is §5.4's lesson on a
/// different subject.
///
/// <para>This walk, and not the MFT, is deliberately the only route. The file record does carry a
/// hard-link count (at 0x12, which <see cref="Mft.MftRecordHeader.Read"/> leaves unread), but the
/// table is only readable elevated, and a link-aware figure that existed only under elevation would
/// disagree with the unelevated one for the same tree. <c>GetFileInformationByHandleEx</c> answers
/// per file at either privilege level — observed on a real hard link, unelevated, before this was
/// built — so one route serves both and the number never depends on how the app was launched.
/// <see cref="ScanResult.Fallback"/> is therefore <see cref="FallbackReason.None"/>: elevating
/// would not change anything, and the offer would be false.</para>
///
/// <para>A file with several links all inside the tree is not counted, though deleting the whole
/// tree would free it. Telling that case apart means enumerating every link's name per multi-linked
/// file, for stores whose layout never produces it — content-addressed files link outward, not to
/// each other — so the cheap reading under-reports in the rare case rather than over-reporting in
/// any, which is the direction §5.4 allows.</para>
/// </summary>
public sealed partial class HardLinkAwareScanner : IDirectoryScanner
{
    /// <summary>Stateless, so a single instance serves the process (G5).</summary>
    public static readonly HardLinkAwareScanner Default = new();

    private HardLinkAwareScanner()
    {
    }

    public ValueTask<ScanResult> MeasureAsync(
        string path,
        MinimumAge keep = default,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default) =>
        new(Task.Run(() => MeasureNow(path, keep, progress, ct), ct));

    private static ScanResult MeasureNow(
        string path,
        MinimumAge keep,
        IProgress<ScanSize>? progress,
        CancellationToken ct)
    {
        if (TryMeasureFile(path, keep) is { } file)
        {
            return ScanResult.Direct(file.Size, file.WithheldRecent);
        }

        var walked = Measure(path, keep, progress, ct);

        return ScanResult.ByChoice(walked.Size, walked.WithheldRecent);
    }

    /// <summary>Always null — this scanner holds no index. See <see cref="ParallelEnumerationScanner"/>.</summary>
    public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesNamedAsync(
        string name,
        string root,
        CancellationToken ct = default) => new((IReadOnlyList<string>?)null);

    /// <summary>Nothing is remembered here, so every reading is already taken from the disk.</summary>
    public ValueTask<ScanResult> MeasureFromDiskAsync(string path, CancellationToken ct = default) =>
        MeasureAsync(path, MinimumAge.Off, progress: null, ct);

    /// <summary>Nothing is retained between calls, so there is nothing to drop.</summary>
    public void Invalidate()
    {
    }

    private static (ScanSize Size, bool WithheldRecent) Measure(
        string path,
        MinimumAge keep,
        IProgress<ScanSize>? progress,
        CancellationToken ct)
    {
        if (!LongPath.DirectoryExists(path))
        {
            return (Approximate(0, 0), false);
        }

        long allocated = 0;
        long logical = 0;

        // See ParallelEnumerationScanner for why this is an int: the walk sets it from several
        // threads, and Interlocked has no bool overload.
        var withheld = 0;

        BoundedFileWalk.Visit(
            LongPath.Extended(path),
            file =>
            {
                // Asked before the handle is opened, because the guard is the cheaper question and
                // the answer is the same either way: a file it keeps is one this store's eviction
                // will not take, so its sole-linked bytes are not reclaimable here.
                if (keep.Protects(file))
                {
                    Interlocked.Exchange(ref withheld, 1);
                    return;
                }

                if (TryQuery(file.FullName) is { NumberOfLinks: 1 } sole)
                {
                    Interlocked.Add(ref allocated, sole.AllocationSize);
                    Interlocked.Add(ref logical, sole.EndOfFile);
                }
            },
            // §5.5: stream partial results. One report per breadth-first level, not per file.
            () => progress?.Report(Approximate(
                Interlocked.Read(ref allocated), Interlocked.Read(ref logical))),
            ct);

        return (
            Approximate(Interlocked.Read(ref allocated), Interlocked.Read(ref logical)),
            Volatile.Read(ref withheld) == 1);
    }

    /// <summary>
    /// Both numbers here are read exactly, and the result is still marked approximate, because the
    /// claim it feeds is a prediction: link counts move whenever a project installs or removes
    /// dependencies, and the tool's eviction chooses by its own reference records rather than by
    /// counting links. A figure the next hour can change must not present itself as precise.
    /// </summary>
    private static ScanSize Approximate(long allocated, long logical) =>
        new(allocated, logical, IsApproximate: true);

    /// <summary>
    /// The sole-link size of <paramref name="path"/> if it is a file, or null for anything else,
    /// mirroring <see cref="ParallelEnumerationScanner"/>: a single named file is a legitimate
    /// subject, and answering zero for one would make its step unofferable.
    /// </summary>
    private static (ScanSize Size, bool WithheldRecent)? TryMeasureFile(string path, MinimumAge keep)
    {
        if (!LongPath.FileExists(path))
        {
            return null;
        }

        if (keep.ProtectsFile(path))
        {
            return (Approximate(0, 0), true);
        }

        return (
            TryQuery(LongPath.Extended(path)) is { } info
                ? Approximate(
                    info.NumberOfLinks == 1 ? info.AllocationSize : 0,
                    info.NumberOfLinks == 1 ? info.EndOfFile : 0)
                : Approximate(0, 0),
            false);
    }

    /// <summary>
    /// One file's link count and both sizes, from a single attributes-only handle, or null where
    /// the file could not be opened — which skips the file, under-reporting rather than guessing.
    /// </summary>
    private static FileStandardInfo? TryQuery(string extendedPath)
    {
        using var handle = CreateFile(
            extendedPath,
            FileReadAttributes,
            ShareAll,
            securityAttributes: 0,
            OpenExisting,
            OpenReparsePoint,
            templateFile: 0);

        if (handle.IsInvalid)
        {
            return null;
        }

        return GetFileInformationByHandleEx(
            handle, FileStandardInfoClass, out var info, Marshal.SizeOf<FileStandardInfo>())
                ? info
                : null;
    }

    private const uint FileReadAttributes = 0x0080;
    private const uint ShareAll = 0x0007;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const int FileStandardInfoClass = 1;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileStandardInfo
    {
        public readonly long AllocationSize;
        public readonly long EndOfFile;
        public readonly uint NumberOfLinks;
        public readonly byte DeletePending;
        public readonly byte Directory;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileStandardInfo information,
        int bufferSize);
}
