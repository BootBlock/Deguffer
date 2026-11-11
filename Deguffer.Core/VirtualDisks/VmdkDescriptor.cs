using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.VirtualDisks;

/// <summary>What a VMware disk's descriptor says about the files the disk is made of.</summary>
/// <param name="Parts">
/// The files that hold the disk's data beside the one its machine names, or null where the descriptor
/// could not be read.
/// </param>
/// <param name="Parent">The disk this one records changes to, for a snapshot's delta disk, or null.</param>
internal sealed record VmdkDescription(IReadOnlyList<string>? Parts, string? Parent)
{
    public static VmdkDescription Unreadable { get; } = new(null, null);

    /// <summary>A disk that is one file and the base of its chain, which is also what a missing file is described as.</summary>
    public static VmdkDescription Whole { get; } = new([], null);
}

/// <summary>
/// Reads a VMware disk's descriptor: the text that names the disk's extents and, for a delta disk, its
/// parent.
///
/// <para>The format is VMware's Virtual Disk Format specification, as libyal's libvmdk documentation
/// and QEMU's <c>block/vmdk.c</c> read it. A disk split into several files, which Workstation offers
/// when a disk is made, is a text file of a kilobyte opening <c># Disk DescriptorFile</c> beside the
/// extents its lines name, so measuring that file alone would report a 40 GB disk as a kilobyte. A disk
/// in one sparse file opens with the magic <c>KDMV</c>, and its descriptor is inside it, at the sector
/// the header's little-endian <c>descriptorOffset</c> at byte 28 names, for the number of sectors
/// <c>descriptorSize</c> at byte 36 names. An extent's file name is relative to the descriptor's folder,
/// and a <c>ZERO</c> extent has no file. <c>parentFileNameHint</c> appears only in a delta disk.</para>
///
/// <para>Its output is file names read from inside the file, so no test can show from its result whether
/// the path it opened with carried the <c>\\?\</c> prefix; it opens with <see cref="LongPath.Extended"/>
/// regardless.</para>
/// </summary>
internal static partial class VmdkDescriptor
{
    private const int Sector = 512;

    /// <summary>A descriptor is a few hundred bytes. One larger than this is not one.</summary>
    private const int MaximumBytes = 64 * 1024;

    private static ReadOnlySpan<byte> SparseMagic => "KDMV"u8;

    private static ReadOnlySpan<byte> TextHeader => "# Disk DescriptorFile"u8;

    /// <summary>
    /// What the descriptor of the disk at <paramref name="path"/> says. A file that is neither kind of
    /// descriptor is a disk of one file: VirtualBox's and Hyper-V's formats, and an extent named directly.
    /// A missing file is described as whole, so the report says it is missing rather than unreadable.
    /// </summary>
    public static VmdkDescription Read(string path)
    {
        try
        {
            using var stream = new FileStream(
                LongPath.Extended(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);

            Span<byte> header = stackalloc byte[Sector];
            var read = stream.ReadAtLeast(header, Sector, throwOnEndOfStream: false);
            header = header[..read];

            if (header.StartsWith(SparseMagic))
            {
                return read < 44 ? VmdkDescription.Unreadable : Embedded(stream, header, path);
            }

            if (!header.StartsWith(TextHeader))
            {
                return VmdkDescription.Whole;
            }

            stream.Position = 0;

            return BoundedFile.ReadVerbatim(stream, MaximumBytes) is { } text
                ? Decoded(text.Span, path)
                : VmdkDescription.Unreadable;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return VmdkDescription.Whole;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked, refused or cut short while read: the disk's files are not known, which is never the
            // same as its being one file.
            return VmdkDescription.Unreadable;
        }
    }

    private static VmdkDescription Embedded(FileStream stream, ReadOnlySpan<byte> header, string path)
    {
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(header[28..]);
        var size = BinaryPrimitives.ReadUInt64LittleEndian(header[36..]);

        // No descriptor inside: an extent of a split disk, which its own descriptor names.
        if (offset == 0 || size == 0)
        {
            return VmdkDescription.Whole;
        }

        if (size > MaximumBytes / Sector || offset > (ulong)(stream.Length / Sector))
        {
            return VmdkDescription.Unreadable;
        }

        var text = new byte[(int)size * Sector];
        stream.Position = (long)offset * Sector;
        var read = stream.ReadAtLeast(text, text.Length, throwOnEndOfStream: false);

        // The descriptor's space is padded with zeros after its text.
        var length = text.AsSpan(0, read).IndexOf((byte)0) is >= 0 and var end ? end : read;

        return Decoded(text.AsSpan(0, length), path);
    }

    /// <summary>
    /// The descriptor in the encoding its own <c>encoding</c> line names, as Workstation writes it on a
    /// machine whose code page is not UTF-8, or unreadable where that is unknown or the bytes are not that
    /// text: an extent or a parent decoded in the wrong encoding names a file that is not there, and would
    /// be registered, and protected, as absent.
    /// </summary>
    private static VmdkDescription Decoded(ReadOnlySpan<byte> content, string path) =>
        VmwareDictionary.Decode(content, "encoding") is { } text ? Parse(text, path) : VmdkDescription.Unreadable;

    /// <summary>The descriptor's text, read against the folder of the file it came from.</summary>
    internal static VmdkDescription Parse(string text, string path)
    {
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var self = LongPath.Configured(path);

        List<string> parts = [];
        string? parent = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            if (ParentLine().Match(line) is { Success: true } hint)
            {
                parent = RecordedPath.Resolve(folder, VmwareDictionary.Unescape(hint.Groups["file"].Value));
                continue;
            }

            if (ExtentLine().Match(line) is not { Success: true } extent
                || extent.Groups["type"].Value.Equals("ZERO", StringComparison.OrdinalIgnoreCase)
                || RecordedPath.Resolve(folder, extent.Groups["file"].Value) is not { } file)
            {
                continue;
            }

            // A disk in one sparse file names itself as its only extent.
            if (!file.Equals(self, StringComparison.OrdinalIgnoreCase)
                && !parts.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                parts.Add(file);
            }
        }

        return new VmdkDescription(parts, parent);
    }

    [GeneratedRegex("""^(?:RW|RDONLY|NOACCESS)\s+\d+\s+(?<type>[A-Za-z]+)\s+"(?<file>[^"]+)"(?:\s|$)""", RegexOptions.CultureInvariant)]
    private static partial Regex ExtentLine();

    [GeneratedRegex("""^parentFileNameHint\s*=\s*"(?<file>[^"]+)"\s*$""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ParentLine();
}
