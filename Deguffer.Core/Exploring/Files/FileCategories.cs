using System.Collections.Frozen;

namespace Deguffer.Core.Exploring.Files;

/// <summary>
/// Which <see cref="FileCategory"/> a file name falls in, and what each one is called.
///
/// <para>By the name alone, never the contents. <see cref="ExploreTree"/> already holds every name,
/// so asking costs no read of the disk, and a scan of a whole volume is millions of names. A file
/// whose extension says one thing and whose contents say another is described by its extension,
/// which is also what Explorer does.</para>
///
/// <para>A small fixed set, deliberately. A category nobody can predict the members of is a filter
/// nobody can trust, so an extension that could reasonably belong to two is left in the one where
/// it is large: <c>.ts</c> is a video stream here, because a TypeScript source file is never what
/// fills a disk.</para>
/// </summary>
public static class FileCategories
{
    /// <summary>
    /// Every extension this knows, with its leading dot, to the category it names. Built once for
    /// the life of the process (G5), and read by span so that asking about a name allocates nothing.
    /// </summary>
    private static readonly FrozenDictionary<string, FileCategory> ByExtension = Table().ToFrozenDictionary(
        entry => entry.Extension, entry => entry.Category, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, FileCategory>.AlternateLookup<ReadOnlySpan<char>> BySpan =
        ByExtension.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// What an executable's name has to contain to be described as an installer.
    ///
    /// <para>A program and its installer share an extension, and most of the large executables on a
    /// disk are installers somebody downloaded once: <c>VSCodeUserSetup-x64.exe</c>,
    /// <c>install.exe</c>. Still the name and not the contents, so an installer called something
    /// else is <see cref="FileCategory.Other"/>, as every other program is.</para>
    /// </summary>
    private static readonly string[] InstallerWords = ["setup", "install"];

    /// <summary>The categories in the order a picker lists them, built once (G5).</summary>
    public static IReadOnlyList<FileCategory> All { get; } = Enum.GetValues<FileCategory>();

    /// <summary>
    /// The category <paramref name="name"/> falls in. A name with no extension, and one with an
    /// extension this does not know, is <see cref="FileCategory.Other"/>. Case does not matter.
    /// </summary>
    public static FileCategory Of(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var dot = name.LastIndexOf('.');

        // A leading dot is a hidden name on other systems and an extension on this one, which is how
        // Path.GetExtension reads ".gitignore". Either way it is not in the table.
        if (dot < 0 || !BySpan.TryGetValue(name.AsSpan(dot), out var category))
        {
            return IsInstallerProgram(name, dot) ? FileCategory.Installers : FileCategory.Other;
        }

        return category;
    }

    /// <summary>What a category is called where a reader sees it.</summary>
    public static string Label(FileCategory category) => category switch
    {
        FileCategory.Video => "Video",
        FileCategory.Audio => "Audio",
        FileCategory.Images => "Images",
        FileCategory.Documents => "Documents",
        FileCategory.Archives => "Archives",
        FileCategory.DiskImages => "Disk images",
        FileCategory.VirtualMachineDisks => "Virtual machine disks",
        FileCategory.Installers => "Installers",
        FileCategory.CodeAndBuildOutput => "Code and build output",
        FileCategory.Other => "Other",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    /// <summary>
    /// A loop rather than a predicate over <see cref="InstallerWords"/>. A lambda capturing the name
    /// would be allocated on every call, and this is asked of most names in a pass over a volume.
    /// </summary>
    private static bool IsInstallerProgram(string name, int dot)
    {
        if (dot < 0 || !name.AsSpan(dot).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var word in InstallerWords)
        {
            if (name.AsSpan(0, dot).Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(string Extension, FileCategory Category)> Table() =>
    [
        .. In(FileCategory.Video,
            ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".mpg", ".mpeg", ".flv", ".ts",
            ".m2ts", ".mts", ".3gp", ".vob", ".ogv"),
        .. In(FileCategory.Audio,
            ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aif", ".aiff", ".ape",
            ".mid", ".midi"),
        .. In(FileCategory.Images,
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif",
            ".avif", ".jxl", ".ico", ".svg", ".psd", ".raw", ".dng", ".cr2", ".cr3", ".nef", ".arw",
            ".orf", ".rw2"),
        .. In(FileCategory.Documents,
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf",
            ".txt", ".md", ".csv", ".epub"),
        .. In(FileCategory.Archives,
            ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".zst",
            ".lz", ".lzma", ".cab"),
        .. In(FileCategory.DiskImages, ".iso", ".img", ".dmg", ".wim", ".esd", ".nrg", ".mdf"),
        .. In(FileCategory.VirtualMachineDisks, ".vhdx", ".vhd", ".avhdx", ".avhd", ".vmdk", ".vdi", ".qcow2"),
        .. In(FileCategory.Installers, ".msi", ".msp", ".msu", ".msix", ".msixbundle", ".appx", ".appxbundle"),
        .. In(FileCategory.CodeAndBuildOutput,
            ".cs", ".vb", ".fs", ".c", ".cc", ".cpp", ".h", ".hpp", ".java", ".kt", ".py", ".js",
            ".mjs", ".jsx", ".tsx", ".go", ".rs", ".rb", ".php", ".swift", ".dart", ".lua", ".ps1",
            ".sh", ".sql", ".obj", ".o", ".pdb", ".ilk", ".pch", ".ipch", ".idb", ".lib", ".a",
            ".class", ".pyc", ".tlog"),
    ];

    private static IEnumerable<(string, FileCategory)> In(FileCategory category, params string[] extensions) =>
        extensions.Select(extension => (extension, category));
}
