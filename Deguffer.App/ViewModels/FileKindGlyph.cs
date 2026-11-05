using System.Collections.Frozen;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Segoe Fluent Icons glyph that says what kind of file a name is, by its extension alone, so a
/// list of groups can be scanned by kind before it is read. A glyph and never a thumbnail: drawing a
/// thumbnail opens the file, and opening a cloud file can download it, which §7.4 forbids.
/// </summary>
public static class FileKindGlyph
{
    private const string Picture = "";
    private const string Video = "";
    private const string Audio = "";
    private const string Document = "";
    private const string Archive = "";
    private const string Code = "";
    private const string Program = "";
    private const string Font = "";

    /// <summary>The glyph for a file whose extension names none of the kinds below, or that has none.</summary>
    public const string Other = "";

    private static readonly FrozenDictionary<string, string> ByExtension = Kinds(
        (Picture, ["jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "avif", "raw", "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2", "svg", "ico", "psd", "jxl"]),
        (Video, ["mp4", "m4v", "mov", "avi", "mkv", "wmv", "webm", "mpg", "mpeg", "3gp", "mts", "m2ts", "flv"]),
        (Audio, ["mp3", "flac", "wav", "m4a", "aac", "ogg", "opus", "wma", "aiff", "aif", "alac", "mid", "midi"]),
        (Document, ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf", "txt", "md", "csv", "epub", "pages", "numbers", "key"]),
        (Archive, ["zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "zst", "cab", "iso", "img", "vhd", "vhdx", "dmg"]),
        (Code, ["cs", "js", "ts", "tsx", "jsx", "py", "java", "c", "cpp", "h", "hpp", "rs", "go", "json", "xml", "xaml", "yaml", "yml", "html", "css", "ps1", "sh", "sql"]),
        (Program, ["exe", "msi", "msix", "appx", "dll", "sys", "bat", "cmd"]),
        (Font, ["ttf", "otf", "woff", "woff2", "fon"]));

    /// <summary>The glyph for <paramref name="name"/>, a file name or a path, by its extension.</summary>
    public static string Of(string name)
    {
        var extension = Path.GetExtension(name.AsSpan());

        return extension.Length > 1 && ByExtension.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(extension[1..], out var glyph)
            ? glyph
            : Other;
    }

    private static FrozenDictionary<string, string> Kinds(params (string Glyph, string[] Extensions)[] kinds) =>
        kinds
            .SelectMany(kind => kind.Extensions.Select(extension => (extension, kind.Glyph)))
            .ToFrozenDictionary(pair => pair.extension, pair => pair.Glyph, StringComparer.OrdinalIgnoreCase);
}
