using Deguffer.Core.Providers;

namespace Deguffer.Core.Safety;

/// <summary>
/// Checks a candidate directory against a <see cref="BuildDirectoryKind"/>, on disk.
///
/// Separate from the declaration it applies for §6.4's G2: the kind is a table a reader can audit,
/// and this is the one place that touches the filesystem to test it. Only directory entries are
/// read; no file is opened.
/// </summary>
public static class BuildDirectorySignature
{
    /// <summary>
    /// The project folder <paramref name="directory"/> belongs to, or null if its identity cannot be
    /// established — in which case the caller must leave it alone.
    /// </summary>
    public static string? TryRecognise(BuildDirectoryKind kind, string directory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var project = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));

        if (string.IsNullOrEmpty(project))
        {
            // A volume root has no project around it, so there is nothing to recognise it by.
            return null;
        }

        if (LongPath.IsReparsePoint(directory) || LongPath.IsReparsePoint(project))
        {
            // A junction here would let a deletion escape the directory that was examined.
            return null;
        }

        ct.ThrowIfCancellationRequested();

        foreach (var sibling in kind.RequiredSiblings)
        {
            if (!Exists(Path.Combine(project, sibling)))
            {
                return null;
            }
        }

        foreach (var content in kind.RequiredContents)
        {
            if (!Exists(Path.Combine(directory, content)))
            {
                return null;
            }
        }

        if (kind.AnyOfSiblings.Count > 0
            && !kind.AnyOfSiblings.Any(sibling => Exists(Path.Combine(project, sibling))))
        {
            return null;
        }

        return project;
    }

    /// <summary>
    /// Whether the entry is there, as either a file or a directory.
    ///
    /// Deliberately not typed. Unity's <c>Packages</c> is a folder and Rust's <c>Cargo.toml</c> is a
    /// file, and the distinction carries no safety weight: what a marker proves is that the
    /// toolchain has been here, and a toolchain that changed one to the other would be reported
    /// unrecognised for a reason nobody could act on.
    /// </summary>
    private static bool Exists(string path) => LongPath.FileExists(path) || LongPath.DirectoryExists(path);
}
