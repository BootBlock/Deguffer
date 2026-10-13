using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// <see cref="ScanBounds.LeftOut"/> put in the terms one measurement of one root asks it in: each
/// entry as its name in the listing of the folder holding it, and as its components below the root.
///
/// <para><b>Built once a measurement, so a walk pays one lookup a folder.</b> A temporary folder runs
/// to hundreds of thousands of entries, and almost none of its folders holds anything left out. A
/// walk asks <see cref="NamesIn"/> once for each listing and, where it answers null, asks nothing of
/// that listing's entries, so no entry's full path is built to be compared (G4).</para>
///
/// <para><b>Each path is placed below the root by the place it names, then spelled the way the walk
/// spells it.</b> Both sides are put in <see cref="ReachedFolder.Comparable"/> form first, so a root reached
/// through <c>C:\Users\LONGPR~1</c> and an entry a program reported under the long name are still one
/// place. The keys are then built from the root exactly as the walk lists it, with
/// <see cref="Path.Join(string?, string?)"/> as <see cref="WalkEntry.FullName"/> joins, and compared
/// ignoring case, as <see cref="Execution.RemovalBounds"/> compares.</para>
/// </summary>
internal sealed class LeftOutEntries
{
    private readonly Dictionary<string, HashSet<string>> _byFolder;

    private LeftOutEntries(Dictionary<string, HashSet<string>> byFolder, IReadOnlyList<IReadOnlyList<string>> below)
    {
        _byFolder = byFolder;
        Below = below;
    }

    /// <summary>Each entry left out, as its components below the root.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Below { get; }

    /// <summary>
    /// The entries of <paramref name="bounds"/> below <paramref name="root"/>, or null where none is,
    /// so a measurement with nothing to leave out pays nothing for the question.
    /// </summary>
    /// <param name="root">The measured path, in either form.</param>
    public static LeftOutEntries? Of(string root, ScanBounds bounds)
    {
        if (!bounds.LeavesEntriesOut)
        {
            return null;
        }

        var place = ReachedFolder.Comparable(root);
        var prefix = place.EndsWith(Path.DirectorySeparatorChar) ? place.Length : place.Length + 1;
        var walked = LongPath.Extended(root);
        var byFolder = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var below = new List<IReadOnlyList<string>>();

        foreach (var path in bounds.LeftOut!)
        {
            var entry = ReachedFolder.Comparable(path);

            if (entry.Length <= prefix || !LongPath.Contains(place, entry))
            {
                continue;
            }

            string[] components = entry[prefix..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var folder = components[..^1].Aggregate(walked, Path.Join);

            if (!byFolder.TryGetValue(folder, out var names))
            {
                byFolder[folder] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            if (names.Add(components[^1]))
            {
                below.Add(components);
            }
        }

        return below.Count == 0 ? null : new LeftOutEntries(byFolder, below);
    }

    /// <summary>
    /// The names left out of the listing <paramref name="contents"/> is, or null where it holds none.
    /// An empty listing answers null, as it holds nothing to leave out.
    /// </summary>
    public IReadOnlySet<string>? NamesIn(in DirectoryContents contents) =>
        FolderOf(contents) is { } folder && _byFolder.TryGetValue(folder, out var names) ? names : null;

    /// <summary>
    /// The folder a listing was read from, which every entry of it carries as the one shared string.
    /// </summary>
    private static string? FolderOf(in DirectoryContents contents) =>
        contents.Entries.Count > 0 ? contents.Entries[0].Directory
        : contents.Links.Count > 0 ? contents.Links[0].Directory
        : contents.ReparseFiles.Count > 0 ? contents.ReparseFiles[0].Directory
        : null;
}
