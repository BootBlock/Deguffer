using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What the first bytes of a file say it is.</summary>
public enum DumpContent
{
    Dump,
    NotDump,

    /// <summary>
    /// The file could not be opened, so nothing is known about it. Never read as either answer: a
    /// refusal is not evidence that a file is a dump, and it is not evidence that it is not one.
    /// </summary>
    Unreadable,
}

/// <summary>
/// Whether a file is a crash dump, read from its header rather than from its name.
///
/// <para>Asked of a file in a place a setting names, which may be any folder on the machine. A name
/// is something anything can carry, and a header is something only a dump writer writes: a
/// user-mode minidump starts with <c>MDMP</c>, and a kernel dump with <c>PAGEDUMP</c>, or
/// <c>PAGEDU64</c> on a 64-bit machine. Every dump Windows writes, from a minidump to a complete
/// memory dump, carries one of the three.</para>
/// </summary>
internal static class DumpSignature
{
    public static DumpContent Of(string path)
    {
        Span<byte> header = stackalloc byte[8];
        int read;

        try
        {
            using var stream = new FileStream(
                LongPath.Extended(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A kernel dump is readable only by an administrator, and a dump still being written is
            // held open by the writer. Both are ordinary.
            return DumpContent.Unreadable;
        }

        ReadOnlySpan<byte> start = header[..read];

        return start.StartsWith("MDMP"u8) || start.StartsWith("PAGEDUMP"u8) || start.StartsWith("PAGEDU64"u8)
            ? DumpContent.Dump
            : DumpContent.NotDump;
    }
}
