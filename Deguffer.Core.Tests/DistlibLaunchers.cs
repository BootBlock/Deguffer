using System.IO.Compression;
using System.Text;

namespace Deguffer.Core.Tests;

/// <summary>
/// Script launchers laid out as distlib writes them: an executable, a shebang line, and a zip archive
/// appended at the end.
/// </summary>
internal static class DistlibLaunchers
{
    /// <summary>
    /// A launcher whose shebang line is <paramref name="shebang"/>, written whole. The executable before
    /// it holds a line feed and a stray <c>#!</c>, as a binary may, so a reader that searched the whole
    /// file rather than the line before the archive would find the wrong one.
    /// </summary>
    public static byte[] Bytes(string shebang)
    {
        byte[] executable = [0x4D, 0x5A, 0x90, 0x00, (byte)'#', (byte)'!', 0x01, (byte)'\n', 0xFF, 0x00, 0x12];

        return [.. executable, .. Encoding.UTF8.GetBytes(shebang), .. Archive()];
    }

    /// <summary>A launcher naming <paramref name="interpreter"/>, quoted as distlib quotes a path holding a space.</summary>
    public static void Write(string path, string interpreter)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var quoted = interpreter.Contains(' ', StringComparison.Ordinal) ? $"\"{interpreter}\"" : interpreter;
        File.WriteAllBytes(path, Bytes($"#!{quoted}\r\n"));
    }

    /// <summary>The script pip's launcher carries, as a zip archive.</summary>
    public static byte[] Archive()
    {
        using var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("__main__.py").Open());
            writer.Write("import sys\nfrom pip._internal.cli.main import main\nsys.exit(main())\n");
        }

        return stream.ToArray();
    }
}
