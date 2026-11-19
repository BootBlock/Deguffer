using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Safety;

/// <summary>
/// A file's named streams, listed through a handle already held on it, for a duplicate's comparison
/// (§7.4) to find the streams of the copy removed and the copy kept the same.
/// </summary>
internal sealed unsafe partial class FileInformation
{
    /// <summary><c>FILE_NAMED_STREAMS</c>: the volume can hold streams beside a file's content.</summary>
    private const uint NamedStreamsSupported = 0x0004_0000;

    /// <summary>The listing of one file's streams read into the stack, which holds every listing of a file with a few streams.</summary>
    private const int FirstStreamListing = 4096;

    /// <summary>The most a listing of one file's streams may take before Windows' answer is given up on.</summary>
    private const int LongestStreamListing = 16 * 1024 * 1024;

    /// <summary><c>FILE_STREAM_INFO</c>'s fixed part: the next entry's offset, the name's length, the stream's size and allocation.</summary>
    private const int StreamEntryHeader = 24;

    /// <summary>How Windows names a file's main, unnamed stream in a listing of its streams.</summary>
    private const string MainStream = "::$DATA";

    /// <summary>
    /// Every named stream of the file <paramref name="handle"/> is open on, as Windows names them
    /// (<c>:name:$DATA</c>) with their lengths, its main stream and <paramref name="leftOut"/> left
    /// out, or null where Windows would not list them. Asked through a handle already held, so the
    /// streams listed are that file's whatever its path names by then. An attributes-only handle
    /// answers it too, measured on NTFS.
    ///
    /// <para>A removal lists a file's streams several times, and most files hold none, so the
    /// listing is read into the stack and only a listing that does not fit there rents a larger
    /// buffer. A file with no named stream allocates nothing.</para>
    /// </summary>
    /// <param name="leftOut">A stream left out of the listing, matched without regard to case as NTFS names streams.</param>
    public static IReadOnlyList<NamedStream>? StreamsOf(SafeFileHandle handle, string leftOut)
    {
        if (TryListStreams(handle, stackalloc byte[FirstStreamListing], leftOut, out var streams))
        {
            return streams;
        }

        for (var size = FirstStreamListing * 2; size <= LongestStreamListing; size *= 2)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(size);

            try
            {
                if (TryListStreams(handle, buffer.AsSpan(0, size), leftOut, out streams))
                {
                    return streams;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether Windows answered a listing of the streams into <paramref name="buffer"/>, with the
    /// <paramref name="streams"/> it gave (null where it refused), or false where they did not fit.
    /// </summary>
    private static bool TryListStreams(
        SafeFileHandle handle, Span<byte> buffer, string leftOut, out IReadOnlyList<NamedStream>? streams)
    {
        fixed (byte* bytes = buffer)
        {
            if (GetFileInformationByHandleEx(handle, FileStreamInfoClass, bytes, buffer.Length))
            {
                streams = Streams(bytes, buffer.Length, leftOut);
                return true;
            }
        }

        var error = Marshal.GetLastPInvokeError();

        switch (error)
        {
            case ErrorHandleEof:
                // Nothing holds data: a folder.
                streams = [];
                return true;

            case ErrorMoreData:
                streams = null;
                return false;

            default:
                streams = StreamsWhereRefused(error, VolumeFlagsOf(handle));
                return true;
        }
    }

    /// <summary>
    /// The named streams of a file whose file system refused <c>FileStreamInfo</c> with
    /// <paramref name="error"/>, given what its volume says it supports: none, where the volume says it
    /// holds no named streams, which FAT32 and exFAT say and refuse the class with
    /// <c>ERROR_INVALID_PARAMETER</c> (measured on 2026-10-09). Null everywhere else, because a volume
    /// that may hold named streams and would not list them may hold one the copy kept lacks.
    /// </summary>
    /// <param name="volumeFlags">The volume's <c>FILE_*</c> flags, or null where it would not say.</param>
    internal static IReadOnlyList<NamedStream>? StreamsWhereRefused(int error, uint? volumeFlags) =>
        error == ErrorInvalidParameter && volumeFlags is { } flags && (flags & NamedStreamsSupported) == 0 ? [] : null;

    /// <summary>The <c>FILE_*</c> flags of the volume <paramref name="handle"/> is open on, or null where Windows would not say.</summary>
    private static uint? VolumeFlagsOf(SafeFileHandle handle) =>
        GetVolumeInformationByHandle(handle, null, 0, null, null, out var flags, null, 0) ? flags : null;

    /// <summary>
    /// The entries of a <c>FILE_STREAM_INFO</c> listing but the main stream and <paramref name="leftOut"/>,
    /// or null where one runs past the buffer. A name is made a string only once it is kept.
    /// </summary>
    private static IReadOnlyList<NamedStream>? Streams(byte* bytes, int size, string leftOut)
    {
        List<NamedStream>? streams = null;
        var offset = 0;

        while (true)
        {
            var entry = bytes + offset;
            var next = *(uint*)entry;
            var nameBytes = *(uint*)(entry + 4);

            if (offset + StreamEntryHeader + nameBytes > size)
            {
                return null;
            }

            var name = new ReadOnlySpan<char>(entry + StreamEntryHeader, (int)nameBytes / sizeof(char));

            if (!name.SequenceEqual(MainStream) && !name.Equals(leftOut, StringComparison.OrdinalIgnoreCase))
            {
                (streams ??= []).Add(new NamedStream(name.ToString(), *(long*)(entry + 8)));
            }

            if (next == 0)
            {
                return streams ?? (IReadOnlyList<NamedStream>)[];
            }

            offset += (int)next;
        }
    }
}
