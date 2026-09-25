using Deguffer.Core.Providers;

namespace Deguffer.Core.Safety;

/// <summary>A build directory whose identity is established, and what established it.</summary>
/// <param name="Project">The project folder the directory belongs to.</param>
/// <param name="SiblingFiles">
/// The files beside the directory that <see cref="BuildDirectoryKind.RequiredSiblingExtensions"/>
/// matched, by name. Carried out of the recognition so that naming them as survivors does not list
/// the project folder a second time (G4).
/// </param>
public sealed record BuildDirectoryRecognition(string Project, IReadOnlyList<string> SiblingFiles);

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
    public static BuildDirectoryRecognition? TryRecognise(BuildDirectoryKind kind, string directory, CancellationToken ct = default)
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

        var siblingFiles = new List<string>();

        foreach (var extension in kind.RequiredSiblingExtensions)
        {
            var matched = SiblingFiles(project, extension);

            if (matched.Count == 0)
            {
                return null;
            }

            siblingFiles.AddRange(matched);
        }

        return new BuildDirectoryRecognition(project, siblingFiles);
    }

    /// <summary>
    /// The names of the files in <paramref name="project"/> whose extension is
    /// <paramref name="extension"/>. Empty where there are none, and where the folder would not be
    /// listed: a refusal is not evidence of a project (§5.2).
    ///
    /// <para>Each name is compared with its own extension rather than handed to Windows as a
    /// wildcard, whose matching has rules of its own for dots and short names. A backup called
    /// <c>Game.uproject.bak</c> is not a descriptor.</para>
    /// </summary>
    private static IReadOnlyList<string> SiblingFiles(string project, string extension)
    {
        try
        {
            return
            [
                .. new DirectoryInfo(LongPath.Extended(project))
                    .EnumerateFiles()
                    .Where(file => file.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))
                    .Select(file => file.Name),
            ];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Refused, or gone since the candidate was found. Either way nothing was seen.
            return [];
        }
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
