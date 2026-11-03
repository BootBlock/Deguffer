using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What one planning pass found in each ESP-IDF tools folder: the archives in its <c>dist</c>, and
/// everything around them that must survive (§5.2, §5.6).
///
/// <para><b>Only an archive, by the name it is downloaded under.</b> <c>idf_tools.py</c> saves each
/// download under the file name of its URL, or the <c>rename_dist</c> name <c>tools.json</c> gives, and
/// unpacks it only where the name ends in one of the forms <c>unpack</c> accepts: <c>.zip</c>,
/// <c>.tar.gz</c>, <c>.tgz</c>, <c>.tar.xz</c> and <c>.tar.bz2</c>. It writes a download to the same name
/// with <c>.tmp</c> added and renames it into place, so a <c>.tmp</c> of one of those names is a download
/// that did not finish. The Installation Manager saves under the URL's file name too. A folder, a link
/// and a file of any other name in <c>dist</c> are Tier 4.</para>
///
/// <para><b>Everything beside <c>dist</c> survives.</b> The tools folder is listed once, and each entry
/// other than <c>dist</c> is named as a survivor: <c>tools</c>, which every ESP-IDF version on the
/// machine shares, <c>python_env</c>, <c>idf-env.json</c>, <c>eim_idf.json</c>, <c>frameworks</c>, each
/// ESP-IDF checkout and each <c>espidf.constraints.*.txt</c>, and anything nobody classified.</para>
/// </summary>
/// <param name="Scan">Each tools folder and its <c>dist</c>, checked for a link and a refusal on the way down.</param>
/// <param name="Reached">Each <c>dist</c> that was listed, with the archives in it.</param>
/// <param name="Kept">Every entry in and beside each <c>dist</c> that is not an archive, with why it survives.</param>
/// <param name="Listed">What the user is told about a link in <c>dist</c>, or a <c>dist</c> that would not be listed.</param>
/// <param name="Unlisted">Whether a <c>dist</c> was described and would not be listed.</param>
/// <param name="DeclinedLinks">Whether a link in a <c>dist</c> was left alone.</param>
/// <param name="Archives">Every archive in <paramref name="Reached"/>, in one list.</param>
internal sealed record EspIdfArchives(
    DeclaredLocationScan Scan,
    IReadOnlyList<(string Downloads, IReadOnlyList<DeletionTarget> Archives)> Reached,
    IReadOnlyList<(string Path, string Reason)> Kept,
    IReadOnlyList<PlanNote> Listed,
    bool Unlisted,
    bool DeclinedLinks,
    IReadOnlyList<DeletionTarget> Archives)
{
    private const string PartialExtension = ".tmp";

    private const string ArchiveReason =
        "A tool archive ESP-IDF downloaded and unpacked into its tools folder. It is read again only to "
        + "install the same version of that tool again, which downloads it again.";

    private const string PartialReason =
        "A download of a tool archive that ESP-IDF did not finish. The next install of that tool downloads "
        + "it again from the start.";

    private const string DownloadsReason =
        "The folder ESP-IDF downloads its tool archives into. Only the archives inside it are removed.";

    private const string UnrecognisedDownload =
        "Not a tool archive ESP-IDF downloads, so it is left alone.";

    private const string UnrecognisedEntry =
        "Not one of ESP-IDF's downloaded archives, so it is left alone.";

    private const string LinkReason =
        "A link rather than a file, so what it points at was never classified.";

    private static readonly string[] ArchiveExtensions = [".zip", ".tar.gz", ".tgz", ".tar.xz", ".tar.bz2"];

    private static readonly IReadOnlyDictionary<string, string> NamedEntries =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tools"] = "The tools ESP-IDF installed: the compilers, debuggers and utilities every ESP-IDF "
                + "version on this machine shares.",
            ["python_env"] = "The Python environments ESP-IDF builds with.",
            ["frameworks"] = "The copies of ESP-IDF the ESP-IDF installer downloaded.",
            ["idf-env.json"] = "The ESP-IDF installer's record of what it installed.",
            [EspIdfLayout.InstallationRecord] = "The ESP-IDF Installation Manager's record of each ESP-IDF it installed.",
        };

    /// <summary>
    /// Whether <paramref name="name"/> is one an ESP-IDF installer gives a downloaded archive, or the
    /// download of one that did not finish.
    /// </summary>
    public static bool IsArchiveName(string name)
    {
        var archive = name.EndsWith(PartialExtension, StringComparison.OrdinalIgnoreCase) ? name[..^PartialExtension.Length] : name;

        return ArchiveExtensions.Any(extension =>
            archive.Length > extension.Length && archive.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What the user is told: the scan's own notes, and any folder that would not be listed.</summary>
    public IEnumerable<PlanNote> Notes => Scan.Notes.Concat(Listed);

    /// <summary>A folder declined as a link, an entry in <c>dist</c> declined as one, or a folder Windows would not describe or list.</summary>
    public bool LeftSomethingOut => Scan.Declined.Count > 0 || DeclinedLinks || Unreadable;

    public bool Unreadable => Scan.CouldNotBeReached || Unlisted;

    public static EspIdfArchives Of(IReadOnlyList<EspIdfToolsFolder> folders, CancellationToken ct)
    {
        // Declared as a folder emptied in place so that the tools folder and dist are checked for a link,
        // and so that dist is asserted to survive. Only the archives in it are ever planned.
        var scan = DeclaredLocations.Examine(
            [
                .. folders.Select(folder => new DeclaredRoot(
                    folder.Path,
                    folder.Reason,
                    RequiresElevation: false,
                    [new DeclaredLocation(EspIdfLayout.DownloadFolder, DownloadsReason, DeclaredLocationKind.DirectoryContents, ReportsAge: false)],
                    [])),
            ],
            ct);

        var targeted = scan.Targets.Select(target => target.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reached = new List<(string Downloads, IReadOnlyList<DeletionTarget> Archives)>();
        var kept = new List<(string Path, string Reason)>();
        var listed = new List<PlanNote>();
        var unlisted = false;
        var linked = false;

        foreach (var folder in folders.Where(folder => targeted.Contains(folder.Downloads)))
        {
            ct.ThrowIfCancellationRequested();

            if (FolderEntries.Of(folder.Downloads) is not { } downloads)
            {
                unlisted = true;
                listed.Add(UnreadableRoot.Note(LongPath.Display(folder.Downloads)));
                continue;
            }

            reached.Add((folder.Downloads, Classify(folder.Downloads, downloads, kept, listed, ref linked, ct)));

            // The tools folder was described, so a listing that fails here leaves only its unnamed
            // entries unasserted, and the folder itself is already a survivor.
            foreach (var entry in FolderEntries.Of(folder.Path) ?? [])
            {
                if (!entry.Name.Equals(EspIdfLayout.DownloadFolder, StringComparison.OrdinalIgnoreCase))
                {
                    kept.Add((LongPath.Display(entry.FullName), ReasonFor(entry.Name)));
                }
            }
        }

        return new EspIdfArchives(scan, reached, kept, listed, unlisted, linked, [.. reached.SelectMany(found => found.Archives)]);
    }

    private static IReadOnlyList<DeletionTarget> Classify(
        string downloads,
        IReadOnlyList<FileSystemInfo> entries,
        List<(string Path, string Reason)> kept,
        List<PlanNote> notes,
        ref bool linked,
        CancellationToken ct)
    {
        var heading = LongPath.Display(downloads);
        var archives = new List<DeletionTarget>();

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                linked = true;
                kept.Add((path, LinkReason));
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{path}' alone: it is a link to somewhere else, and Deguffer does not look through a link."));
            }
            else if (entry is FileInfo && IsArchiveName(entry.Name))
            {
                archives.Add(new DeletionTarget(
                    path,
                    entry.Name.EndsWith(PartialExtension, StringComparison.OrdinalIgnoreCase) ? PartialReason : ArchiveReason,
                    LastWritten: entry.LastWriteTimeUtc,
                    Kind: TargetKind.File,
                    Group: heading));
            }
            else
            {
                kept.Add((path, UnrecognisedDownload));
            }
        }

        return archives;
    }

    private static string ReasonFor(string name) =>
        NamedEntries.TryGetValue(name, out var reason) ? reason
        : name.StartsWith("espidf.constraints.", StringComparison.OrdinalIgnoreCase)
            ? "The Python package versions one ESP-IDF version builds with."
            : UnrecognisedEntry;
}
