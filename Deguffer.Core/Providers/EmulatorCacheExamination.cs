using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One cache entry the examination recognised, and the emulator whose cache it is.</summary>
internal sealed record EmulatorCacheTarget(EmulatorLayout Layout, string Path, string Reason, TargetKind Kind);

/// <summary>A cache folder the examination read, and the names in it that it recognised.</summary>
internal sealed record EmulatorCacheFolderReading(string Path, IReadOnlySet<string> Recognised);

/// <summary>A root proven to be <paramref name="Layout"/>'s, and the cache folders read in it.</summary>
internal sealed record EmulatorRoot(EmulatorLayout Layout, string Path, IReadOnlyList<EmulatorCacheFolderReading> CacheFolders);

/// <summary>
/// One pass over every emulator's candidate roots: prove each, read each proven root's cache folders,
/// and record what is offered, what survives and why.
///
/// <para>Every path it reaches is reached by name from a proven root, and the only folders it lists
/// are the cache folders a layout names, so nothing beside them is ever enumerated or classified. What
/// survives is named instead, which is how §5.6 asserts it.</para>
/// </summary>
internal sealed class EmulatorCacheExamination
{
    public List<EmulatorRoot> Roots { get; } = [];

    public List<EmulatorCacheTarget> Targets { get; } = [];

    public List<(string Path, string Reason)> Survivors { get; } = [];

    public List<string> Declined { get; } = [];

    public List<PlanNote> Notes { get; } = [];

    public bool Unreadable { get; private set; }

    /// <summary>
    /// No root was proven and there is nothing to say: no link declined, nothing unread, and no
    /// declared folder or refused root to tell the user about.
    /// </summary>
    public bool FoundNothing => Roots.Count == 0 && Declined.Count == 0 && !Unreadable && Notes.Count == 0;

    /// <param name="declaredFolders">The folders the user said an emulator is installed in.</param>
    /// <param name="whyNotOwned">
    /// Why a folder must not be treated as an emulator's, or null where it may be: a drive's root, or
    /// a folder that holds the profile or Windows itself, is never one tool's to answer for, whatever
    /// file sits in it.
    /// </param>
    /// <param name="claimedElsewhere">
    /// Whether another row answers for a declared folder none of <paramref name="layouts"/> proved, so
    /// the plan does not tell the user their RetroArch folder holds no emulator.
    /// </param>
    public static EmulatorCacheExamination Of(
        IReadOnlyList<EmulatorLayout> layouts,
        IReadOnlyList<string> declaredFolders,
        IUserEnvironment environment,
        Func<string, string?> whyNotOwned,
        Func<string, bool> claimedElsewhere,
        CancellationToken ct)
    {
        var examination = new EmulatorCacheExamination();
        var reported = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var provenFrom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var layout in layouts)
        {
            var candidates = layout.FixedRoots(environment)
                .Select(root => (Root: root, Declared: (string?)null))
                .Concat(declaredFolders.SelectMany(folder =>
                    layout.RootsDeclaredBy(folder).Select(root => (Root: root, Declared: (string?)folder))));

            foreach (var (candidate, declared) in candidates)
            {
                ct.ThrowIfCancellationRequested();

                if (LongPath.Configured(candidate) is not { } root)
                {
                    continue;
                }

                // A declared folder can be a fixed root too, and is answered for once.
                var key = layout.Name + "|" + root;

                if (!reported.TryGetValue(key, out var answered))
                {
                    reported[key] = answered = examination.Reports(layout, root, whyNotOwned);
                }

                if (answered && declared is not null)
                {
                    provenFrom.Add(declared);
                }
            }
        }

        foreach (var folder in declaredFolders.Where(folder => !provenFrom.Contains(folder) && !claimedElsewhere(folder)))
        {
            examination.Notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"'{folder}' is one of your emulator folders, but Deguffer found no {Names(layouts)} "
                + "settings file and no RetroArch in it, so nothing in it was looked at."));
        }

        for (var i = 0; i < examination.Roots.Count; i++)
        {
            examination.Roots[i] = examination.Read(examination.Roots[i], whyNotOwned, ct);
        }

        return examination;
    }

    private static string Names(IReadOnlyList<EmulatorLayout> layouts) =>
        string.Join(", ", layouts.SkipLast(1).Select(l => l.Name)) + " or " + layouts[^1].Name;

    /// <summary>
    /// Record <paramref name="root"/> if it is <paramref name="layout"/>'s. A root that is a link, or one
    /// no tool may answer for, is left alone and said so.
    /// </summary>
    /// <returns>
    /// Whether the plan now says something about <paramref name="root"/>, so a declared folder that
    /// Windows would not read is not also reported as holding no emulator.
    /// </returns>
    private bool Reports(EmulatorLayout layout, string root, Func<string, string?> whyNotOwned)
    {
        switch (LongPath.ProbeDirectory(root))
        {
            case PathPresence.Absent:
                return false;

            case PathPresence.Refused:
                Unreached(root);
                return true;
        }

        switch (layout.Proof(root))
        {
            case PathPresence.Absent:
                return false;

            case PathPresence.Refused:
                Unreached(root);
                return true;
        }

        if (LongPath.IsReparsePoint(root))
        {
            DeclineLink(root);
            return true;
        }

        if (whyNotOwned(root) is { } why)
        {
            Notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving '{root}' alone although {layout.Name}'s settings are in it: {why}"));
            return true;
        }

        Roots.Add(new EmulatorRoot(layout, root, []));
        return true;
    }

    private EmulatorRoot Read(EmulatorRoot found, Func<string, string?> whyNotOwned, CancellationToken ct)
    {
        var layout = found.Layout;

        Survivors.Add((found.Path, $"{layout.Name}'s own folder. Only the shader caches inside it are removed."));
        Survivors.AddRange(layout.ProtectedNames.Select(p => (Path.Combine(found.Path, p.Name), p.Reason)));

        var readings = new List<EmulatorCacheFolderReading>();

        foreach (var folder in layout.CacheFoldersIn(found.Path))
        {
            ct.ThrowIfCancellationRequested();

            // A folder a setting moved outside the root is held to the rule a root is, because it is
            // the folder whose entries are about to be classified.
            if (!LongPath.Contains(found.Path, folder.Path) && whyNotOwned(folder.Path) is { } why)
            {
                Notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{folder.Path}' alone although {layout.Name}'s settings name it as its cache: {why}"));
                continue;
            }

            if (ReadFolder(layout, folder, ct) is { } recognised)
            {
                readings.Add(new EmulatorCacheFolderReading(folder.Path, recognised));
            }
        }

        return found with { CacheFolders = readings };
    }

    /// <returns>The names recognised in the folder, or null where it was not read.</returns>
    private HashSet<string>? ReadFolder(EmulatorLayout layout, EmulatorCacheFolder folder, CancellationToken ct)
    {
        switch (LongPath.ProbeDirectory(folder.Path))
        {
            case PathPresence.Absent:
                return null;

            case PathPresence.Refused:
                Unreached(folder.Path);
                return null;
        }

        if (LongPath.IsReparsePoint(folder.Path))
        {
            DeclineLink(folder.Path);
            return null;
        }

        if (FolderEntries.Of(folder.Path) is not { } entries)
        {
            Notes.Add(UnreadableRoot.Note(folder.Path));
            Survivors.Add((folder.Path, UnreadableRoot.UnreachedReason));
            Unreadable = true;
            return null;
        }

        Survivors.Add((folder.Path, $"{layout.Name}'s cache folder itself. Only the shader caches inside it are removed."));
        Survivors.AddRange(folder.ProtectedNames.Select(p => (Path.Combine(folder.Path, p.Name), p.Reason)));

        var recognised = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                DeclineLink(path);
                continue;
            }

            var isDirectory = entry is DirectoryInfo;
            var classification = folder.Classify(entry.Name);

            if (classification.Tier.IsOfferable() && isDirectory == (folder.Kind is TargetKind.Directory))
            {
                recognised.Add(entry.Name);
                Targets.Add(new EmulatorCacheTarget(layout, path, classification.Reason, folder.Kind));
                continue;
            }

            Survivors.Add((path, classification.Tier.IsOfferable()
                ? $"'{entry.Name}' is not the kind of entry {layout.Name} keeps its shader cache in, so it is left alone."
                : classification.Reason));

            // A directory is named in the plan because it is what a user would expect to go with the
            // cache. A loose file is protected all the same, but a note for each would bury the ones
            // that matter.
            if (isDirectory)
            {
                Notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{path}' alone: {Survivors[^1].Reason}"));
            }
        }

        return recognised;
    }

    private void Unreached(string path)
    {
        Notes.Add(UnreadableRoot.UnreachedNote(path));
        Survivors.Add((path, UnreadableRoot.UnreachedReason));
        Unreadable = true;
    }

    private void DeclineLink(string path)
    {
        Declined.Add(path);
        Survivors.Add((path, CacheLevelWalk.LinkReason));
        Notes.Add(CacheLevelWalk.Note(path));
    }
}
