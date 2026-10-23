using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What the places this machine's settings move crash dumps to turned out to hold.</summary>
/// <param name="Targets">Each dump found there, by name. Never a folder.</param>
/// <param name="Protected">
/// What §5.6 asserts survived: each moved folder that was read, the folder holding each moved dump
/// file, each link met, and each place Windows would not describe.
/// </param>
/// <param name="Notes">One sentence for each moved place, saying where it is and what was done there.</param>
/// <param name="Unreachable">Paths Windows would not describe, so nothing here says what is in them.</param>
/// <param name="LeftUnexamined">
/// Whether a moved place may hold a dump Deguffer did not offer: a folder it declined, a link it did
/// not look through, or a file it could not read to confirm.
/// </param>
internal sealed record RelocatedDumpScan(
    IReadOnlyList<DeletionTarget> Targets,
    IReadOnlyList<(string Path, string Reason)> Protected,
    IReadOnlyList<PlanNote> Notes,
    IReadOnlyList<string> Unreachable,
    bool LeftUnexamined)
{
    /// <summary>
    /// Whether a moved place gave the row something to show. A moved folder that is not there, or
    /// holds no dump, does not: it is a setting, and the row is about what is on the disk.
    /// </summary>
    public bool FoundAnything => Targets.Count > 0 || Unreachable.Count > 0 || LeftUnexamined;
}

/// <summary>
/// The crash dumps in the places a setting moves them to, offered one file at a time.
///
/// <para><b>A moved folder is never offered as a folder.</b> <c>MinidumpDir</c> and
/// <c>DumpFolder</c> accept any folder, a drive root and Documents included, and Windows writes its
/// dumps beside whatever else is there. So the folder is a survivor, and the only things offered
/// are the files directly inside it that are named <c>*.dmp</c> and start with a dump's header (see
/// <see cref="DumpSignature"/>). A folder <see cref="ConfiguredFolder.WhyNotOwned"/> declines is not
/// looked into at all: other rows remove things there, or the user keeps their own files there, and
/// a dump the user saved beside them is not a record this row may offer.</para>
///
/// <para><b><c>DumpFile</c> names the one file</b>, so it is offered by that name where its header
/// says it is a dump. A file that cannot be read is left alone rather than offered on its name:
/// a refusal is not evidence of what a file is, and the full kernel dump is readable only by an
/// administrator.</para>
/// </summary>
internal static class RelocatedDumps
{
    private const string LinkReason =
        "A link rather than a folder, so what it points at was never examined.";

    private const string FolderReason =
        "The folder this machine's crash settings name must survive — only the crash dumps directly "
        + "inside it are removed.";

    /// <param name="settings">Every dump location the settings name.</param>
    /// <param name="declared">
    /// Every path the provider already declares. A setting naming one of these, or a place inside
    /// one, is the default and is examined there.
    /// </param>
    public static RelocatedDumpScan Examine(
        IReadOnlyList<CrashDumpSetting> settings,
        IReadOnlyList<string> declared,
        IUserEnvironment environment,
        ISystemDirectories system,
        IVolumeInventory volumes,
        CancellationToken ct = default)
    {
        var scan = new Collector();
        var declaredFolders = declared.Select(path => ReachedFolder.At(path, volumes)).ToList();
        IReadOnlyList<string>? accountTempFolders = null;

        foreach (var unqualified in settings.Where(s => s.Path is null))
        {
            scan.Notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"{unqualified.Setting} sends {unqualified.Holds} to '{unqualified.Value}', which is not "
                + "a full path, so Deguffer cannot tell where it means and looks only in the usual places."));
        }

        var moved = settings
            .Where(s => s.Path is { } path && !declaredFolders.Any(d => d.Holds(ReachedFolder.At(path, volumes))))
            .GroupBy(s => (Path: s.Path!, s.NamesFile), PlaceComparer.Instance);

        foreach (var place in moved)
        {
            ct.ThrowIfCancellationRequested();

            var lead = Lead([.. place]);
            var requiresElevation = place.Any(s => s.RequiresElevation);

            if (place.Key.NamesFile)
            {
                ExamineFile(place.Key.Path, lead, requiresElevation, scan);
                continue;
            }

            accountTempFolders ??= TempRoots.Resolve(environment, system, volumes).AccountFolders;

            if (ConfiguredFolder.WhyNotOwned(place.Key.Path, environment, system, volumes, accountTempFolders) is { } why)
            {
                scan.LeftUnexamined = true;
                scan.Notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"{lead} Deguffer does not look there, because {why}"));
                continue;
            }

            ExamineFolder(place.Key.Path, lead, requiresElevation, scan, ct);
        }

        return new RelocatedDumpScan(
            [.. scan.Targets.Values],
            [.. scan.Protected.DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase)],
            scan.Notes,
            scan.Unreachable,
            scan.LeftUnexamined);
    }

    private static void ExamineFolder(string folder, string lead, bool requiresElevation, Collector scan, CancellationToken ct)
    {
        if (!Reached(folder, lead, scan))
        {
            return;
        }

        switch (LongPath.ProbeDirectory(folder))
        {
            case PathPresence.Absent:
                scan.Notes.Add(new PlanNote(PlanNoteSeverity.Information, $"{lead} There is no such folder now."));
                return;

            case PathPresence.Refused:
                scan.Unreached(folder, lead);
                return;
        }

        if (FolderEntries.Of(folder) is not { } entries)
        {
            scan.Unreached(folder, lead);
            return;
        }

        scan.Protected.Add((folder, FolderReason));

        var unconfirmed = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            // A link is never followed, and a folder is never a dump, whatever either is called.
            if (entry is not FileInfo
                || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || !entry.Name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = LongPath.Display(entry.FullName);

            switch (DumpSignature.Of(path))
            {
                case DumpContent.Dump:
                    scan.Offer(path, entry.LastWriteTimeUtc, requiresElevation);
                    break;

                case DumpContent.Unreadable:
                    unconfirmed++;
                    break;
            }
        }

        var listed = $"{lead} Deguffer offers the crash dumps directly inside it one by one, and never "
            + "the folder or anything else in it.";

        if (unconfirmed > 0)
        {
            scan.LeftUnexamined = true;
            listed += unconfirmed == 1
                ? " It could not read one file named as a dump there to confirm that it is one, so it "
                    + "leaves that file alone."
                : $" It could not read {unconfirmed} files named as dumps there to confirm that they "
                    + "are dumps, so it leaves them alone.";
        }

        scan.Notes.Add(new PlanNote(PlanNoteSeverity.Information, listed));
    }

    private static void ExamineFile(string file, string lead, bool requiresElevation, Collector scan)
    {
        var folder = Path.GetDirectoryName(file);

        if (folder is null || !Reached(folder, lead, scan))
        {
            return;
        }

        switch (LongPath.ProbeFile(file))
        {
            case PathPresence.Absent:
                scan.Notes.Add(new PlanNote(PlanNoteSeverity.Information, $"{lead} There is no dump there now."));
                return;

            case PathPresence.Refused:
                scan.Unreached(file, lead);
                return;
        }

        if (LongPath.IsReparsePoint(file))
        {
            scan.Declined(file, lead);
            return;
        }

        switch (DumpSignature.Of(file))
        {
            case DumpContent.Dump:
                scan.Protected.Add((
                    folder,
                    "The folder holding the dump this machine's crash settings name must survive — "
                    + "only the dump itself is removed."));
                scan.Offer(file, LastWritten(file), requiresElevation);
                scan.Notes.Add(new PlanNote(PlanNoteSeverity.Information, lead));
                return;

            case DumpContent.NotDump:
                scan.Notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"{lead} The file there is not a crash dump, so Deguffer leaves it alone."));
                return;

            default:
                scan.LeftUnexamined = true;
                scan.Notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"{lead} Deguffer could not read the file there to confirm that it is a crash dump, "
                    + "so it leaves it alone. Running Deguffer as administrator lets it read the file."));
                return;
        }
    }

    /// <summary>
    /// Whether every folder from the top of the drive down to <paramref name="folder"/> may be walked,
    /// saying why not where one may not. The setting names an absolute path, so a link at any level
    /// above it moves the place as surely as one at the place itself.
    /// </summary>
    private static bool Reached(string folder, string lead, Collector scan)
    {
        if (Path.GetPathRoot(folder) is not { Length: > 0 } top
            || DerivedPath.FirstObstacleBetween(top, folder) is not { } obstacle)
        {
            return true;
        }

        if (obstacle.IsLink)
        {
            scan.Declined(obstacle.Path, lead);
        }
        else
        {
            scan.Unreached(obstacle.Path, lead);
        }

        return false;
    }

    /// <summary>The sentence that opens every note about one moved place.</summary>
    private static string Lead(IReadOnlyList<CrashDumpSetting> settings) =>
        $"This machine's crash settings ({string.Join(", ", settings.Select(s => s.Setting))}) send "
        + $"{string.Join(" and ", settings.Select(s => s.Holds))} to '{LongPath.Display(settings[0].Path!)}'.";

    private static DateTime? LastWritten(string file)
    {
        try
        {
            return new FileInfo(LongPath.Extended(file)).LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // §7 renders an unknown age as unknown, which is the honest rendering of a refusal.
            return null;
        }
    }

    private sealed class Collector
    {
        public Dictionary<string, DeletionTarget> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Path, string Reason)> Protected { get; } = [];

        public List<PlanNote> Notes { get; } = [];

        public List<string> Unreachable { get; } = [];

        public bool LeftUnexamined { get; set; }

        /// <summary>
        /// One step per dump, however many settings name the place it is in. Where two settings
        /// disagree about who may remove it, the stricter one is the one stated.
        /// </summary>
        public void Offer(string path, DateTime? lastWritten, bool requiresElevation)
        {
            var elevated = requiresElevation || (Targets.TryGetValue(path, out var existing) && existing.RequiresElevation);

            Targets[path] = new DeletionTarget(
                path,
                "A crash dump Windows wrote where this machine's crash settings send them. Each one is "
                + "the record of a single failure and nothing re-creates it.",
                lastWritten,
                TargetKind.File,
                elevated);
        }

        public void Declined(string path, string lead)
        {
            LeftUnexamined = true;
            Protected.Add((path, LinkReason));
            Notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"{lead} Deguffer leaves it alone: '{LongPath.Display(path)}' is a link to somewhere "
                + "else, and Deguffer does not look through a link."));
        }

        public void Unreached(string path, string lead)
        {
            if (Unreachable.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            Unreachable.Add(path);
            Protected.Add((path, UnreadableRoot.UnreachedReason));
            Notes.Add(new PlanNote(PlanNoteSeverity.Information, lead));
            Notes.Add(UnreadableRoot.UnreachedNote(LongPath.Display(path)));
        }
    }

    /// <summary>One place, however its settings spelled it.</summary>
    private sealed class PlaceComparer : IEqualityComparer<(string Path, bool NamesFile)>
    {
        public static readonly PlaceComparer Instance = new();

        public bool Equals((string Path, bool NamesFile) x, (string Path, bool NamesFile) y) =>
            x.NamesFile == y.NamesFile && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Path, bool NamesFile) place) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(place.Path), place.NamesFile);
    }
}
