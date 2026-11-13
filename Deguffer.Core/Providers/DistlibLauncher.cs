using System.Buffers.Binary;
using System.Text;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Python interpreter a Windows script launcher (<c>pip.exe</c>, and every other console script
/// pip installs) starts, read from the launcher itself.
///
/// <para><b>The format.</b> pip writes each script as distlib's launcher: a small executable, then a
/// shebang line naming the interpreter, then a zip archive holding the script, appended at the end of
/// the file. The launcher finds its shebang by finding the archive: the zip's end-of-central-directory
/// record sits at the end of the file and says how long the central directory is and where it starts
/// within the archive, which puts the archive's first byte, and the shebang line ends immediately
/// before it.</para>
///
/// <para><b>Why the interpreter matters.</b> Its folder decides <c>sys.prefix</c>, and pip reads a
/// <c>pip.ini</c> from there, and runs from that prefix's <c>site-packages</c>, so a remembered answer
/// from pip is only as good as what was read about the interpreter.</para>
/// </summary>
internal static class DistlibLauncher
{
    /// <summary>
    /// More than any launcher pip writes: distlib's executables are about a hundred kilobytes and the
    /// archive after them a few hundred bytes. A larger file is not a launcher, and is not read.
    /// </summary>
    internal const int MaximumBytes = 1024 * 1024;

    private const uint EndOfCentralDirectorySignature = 0x06054B50;
    private const int EndOfCentralDirectoryLength = 22;

    /// <summary>
    /// The interpreter <paramref name="launcher"/>'s shebang names, or null where the file could not be
    /// read, is larger than <see cref="MaximumBytes"/>, holds no archive, or holds no shebang naming a
    /// fully qualified path before it (a <c>pip.bat</c>, say).
    /// </summary>
    public static string? InterpreterOf(string launcher) =>
        BoundedFile.ReadVerbatim(launcher, MaximumBytes) is { } content ? InterpreterIn(content.Span) : null;

    /// <summary>The interpreter <paramref name="launcher"/>'s bytes name, as <see cref="InterpreterOf"/> reads them.</summary>
    internal static string? InterpreterIn(ReadOnlySpan<byte> launcher)
    {
        if (ArchiveStart(launcher) is not { } start)
        {
            return null;
        }

        var line = ShebangLine(launcher[..start]);

        return line.IsEmpty ? null : InterpreterNamedBy(line);
    }

    /// <summary>
    /// Where the zip archive at the end of <paramref name="file"/> starts, or null where no
    /// end-of-central-directory record ends the file, or the one there points before its start.
    /// </summary>
    private static int? ArchiveStart(ReadOnlySpan<byte> file)
    {
        // The record is 22 bytes and a comment of up to 65,535 after it, which its last field states;
        // a record is only the one that ends the file where that comment reaches exactly the end.
        var earliest = Math.Max(0, file.Length - EndOfCentralDirectoryLength - ushort.MaxValue);

        for (var at = file.Length - EndOfCentralDirectoryLength; at >= earliest; at--)
        {
            var record = file.Slice(at, EndOfCentralDirectoryLength);

            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != EndOfCentralDirectorySignature
                || at + EndOfCentralDirectoryLength + BinaryPrimitives.ReadUInt16LittleEndian(record[20..]) != file.Length)
            {
                continue;
            }

            long directoryLength = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            long directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            var start = at - directoryLength - directoryOffset;

            return start > 0 ? (int)start : null;
        }

        return null;
    }

    /// <summary>
    /// The shebang's text after <c>#!</c>, from the line that ends at the end of
    /// <paramref name="beforeArchive"/>, or empty where there is none.
    /// </summary>
    private static ReadOnlySpan<byte> ShebangLine(ReadOnlySpan<byte> beforeArchive)
    {
        if (!beforeArchive.EndsWith("\n"u8))
        {
            return [];
        }

        var line = beforeArchive[..^1];

        if (line.EndsWith("\r"u8))
        {
            line = line[..^1];
        }

        // The executable before the line is binary, so the line starts after its last line feed, and
        // the shebang at the last "#!" on it, as the launcher itself finds it by scanning back.
        line = line[(line.LastIndexOf((byte)'\n') + 1)..];
        var shebang = line.LastIndexOf("#!"u8);

        return shebang < 0 ? [] : line[(shebang + 2)..];
    }

    /// <summary>
    /// The interpreter <paramref name="shebang"/> names: the quoted path where distlib quoted one
    /// holding a space, else the text up to the first space, where any options begin.
    /// </summary>
    private static string? InterpreterNamedBy(ReadOnlySpan<byte> shebang)
    {
        string text;

        try
        {
            text = BoundedFile.StrictUtf8.GetString(shebang);
        }
        catch (DecoderFallbackException)
        {
            // distlib writes the line in UTF-8, so bytes that are not are no shebang it wrote.
            return null;
        }

        var path = text.StartsWith('"')
            ? text.IndexOf('"', 1) is var close and > 0 ? text[1..close] : null
            : text.IndexOf(' ') is var space and >= 0 ? text[..space] : text;

        return path is not null && Path.IsPathFullyQualified(path) ? path : null;
    }
}
