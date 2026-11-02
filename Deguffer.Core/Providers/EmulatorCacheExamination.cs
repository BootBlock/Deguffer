using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One cache entry the examination recognised, the emulator whose layout recognised it, and every
/// emulator that proved the folder it is in, any of which holds it back while it runs.
/// </summary>
internal sealed record EmulatorCacheTarget(
    EmulatorLayout Layout, IReadOnlyList<EmulatorLayout> Owners, string Path, string Reason, TargetKind Kind);

/// <summary>
/// A cache folder the examination read, the names in it that it recognised, and the kind of entry
/// each of them was: a layout keeps its cache as folders or as files, never both in one folder.
/// </summary>
internal sealed record EmulatorCacheFolderReading(string Path, IReadOnlySet<string> Recognised, ChildKind Kind)
{
    /// <summary>
    /// Whether <paramref name="child"/> is an entry the plan offered. A link was declined, and an
    /// entry of the other kind was left alone, so the name alone answers neither.
    /// </summary>
    public bool Recognises(ToolRootChild child) => child.Kind == Kind && Recognised.Contains(child.Name);
}

/// <summary>A root proven to be <paramref name="Layout"/>'s, and the cache folders read in it.</summary>
/// <param name="Owners">
/// Every layout that proved this folder, this one included. Each fork of yuzu proves the same
/// <c>user</c> folder where two forks' programs sit beside it, and reads the same caches in it, so
/// while any of them runs the folder is in use.
/// </param>
internal sealed record EmulatorRoot(
    EmulatorLayout Layout,
    string Path,
    IReadOnlyList<EmulatorCacheFolderReading> CacheFolders,
    IReadOnlyList<EmulatorLayout> Owners);

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
    /// The paths a note already names. A level above several cache folders is met once for each of
    /// them, and a folder two layouts proved once for each layout, and either is one thing to say.
    /// </summary>
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Each game's folder entered for its caches. Eden's folder of games is a cache folder too, and a
    /// game's folder met there is answered for by its own caches rather than named as left alone.
    /// </summary>
    private readonly HashSet<string> _gameFolders = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _targeted = new(StringComparer.OrdinalIgnoreCase);

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
    /// <param name="reach">
    /// The folder at a path, at every path it is reachable at, kept by the caller for its planning pass.
    /// A declared folder may name a fixed root, and a setting a cache folder inside its root, through a
    /// letter <c>subst</c> made, or another mount of its volume.
    /// </param>
    public static EmulatorCacheExamination Of(
        IReadOnlyList<EmulatorLayout> layouts,
        IReadOnlyList<string> declaredFolders,
        IUserEnvironment environment,
        Func<string, string?> whyNotOwned,
        Func<string, bool> claimedElsewhere,
        Func<string, ReachedFolder> reach,
        CancellationToken ct)
    {
        var examination = new EmulatorCacheExamination();
        var reported = new List<(EmulatorLayout Layout, ReachedFolder Folder, bool Answered)>();
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

                // A declared folder can be a fixed root too, however each names it, and is answered for
                // once, under the first name.
                var folder = reach(root);
                var known = reported.FindIndex(other => other.Layout == layout && other.Folder.IsSameAs(folder));
                bool answered;

                if (known < 0)
                {
                    answered = examination.Reports(layout, root, whyNotOwned);
                    reported.Add((layout, folder, answered));
                }
                else
                {
                    answered = reported[known].Answered;
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

        var folders = examination.Roots.Select(root => reach(root.Path)).ToList();

        for (var i = 0; i < examination.Roots.Count; i++)
        {
            IReadOnlyList<EmulatorLayout> owners =
            [
                .. examination.Roots.Where((_, j) => folders[j].IsSameAs(folders[i])).Select(root => root.Layout),
            ];

            examination.Roots[i] = examination.Read(examination.Roots[i] with { Owners = owners }, whyNotOwned, reach, ct);
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

        Roots.Add(new EmulatorRoot(layout, root, [], [layout]));
        return true;
    }

    private EmulatorRoot Read(
        EmulatorRoot found, Func<string, string?> whyNotOwned, Func<string, ReachedFolder> reach, CancellationToken ct)
    {
        var layout = found.Layout;

        Survivors.Add((found.Path, $"{layout.Name}'s own folder. Only the shader caches inside it are removed."));
        Survivors.AddRange(layout.ProtectedNames.Select(p => (Path.Combine(found.Path, p.Name), p.Reason)));

        var readings = new List<EmulatorCacheFolderReading>();

        foreach (var listed in layout.CacheFoldersIn(found.Path).Concat(GameCacheFolders(layout, found.Path, ct)))
        {
            ct.ThrowIfCancellationRequested();

            var folder = listed;

            // A path built from the root has had no level checked on the way, and a link at any of
            // them would put the deletion somewhere nothing classified, with every survivor named
            // below it.
            if (LongPath.Contains(found.Path, listed.Path)
                && DerivedPath.FirstObstacleBetween(found.Path, listed.Path) is { } obstacle)
            {
                Obstructed(obstacle);
                continue;
            }

            // A folder inside the root is named as the root is, at any path either is reachable at, so
            // a setting naming it through an alias is still read on the way down from the root. One a
            // setting moved outside the root is held to the rule a root is, because it is the folder
            // whose entries are about to be classified.
            if (reach(found.Path).Naming(reach(listed.Path), found.Path) is { } named)
            {
                folder = listed with { Path = named };
            }
            else if (whyNotOwned(folder.Path) is { } why)
            {
                Notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{folder.Path}' alone although {layout.Name}'s settings name it as its cache: {why}"));
                continue;
            }

            if (ReadFolder(found, folder, ct) is { } recognised)
            {
                readings.Add(new EmulatorCacheFolderReading(
                    folder.Path,
                    recognised,
                    folder.Kind is TargetKind.Directory ? ChildKind.Folder : ChildKind.File));
            }
        }

        return found with { CacheFolders = readings };
    }

    /// <returns>The names recognised in the folder, or null where it was not read.</returns>
    private HashSet<string>? ReadFolder(EmulatorRoot root, EmulatorCacheFolder folder, CancellationToken ct)
    {
        var layout = root.Layout;

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
            Unlisted(folder.Path);
            return null;
        }

        Survivors.Add((folder.Path, $"{layout.Name}'s cache folder itself. Only the shader caches inside it are removed."));
        Survivors.AddRange(folder.ProtectedNames.Select(p => (Path.Combine(folder.Path, p.Name), p.Reason)));

        var recognised = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (_gameFolders.Contains(path))
            {
                continue;
            }

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

                // A folder two layouts proved is read by each, and its cache is one item, whose
                // owners already name both.
                if (_targeted.Add(path))
                {
                    Targets.Add(new EmulatorCacheTarget(layout, root.Owners, path, classification.Reason, folder.Kind));
                }

                continue;
            }

            Survivors.Add((path, classification.Tier.IsOfferable()
                ? $"'{entry.Name}' is not the kind of entry {layout.Name} keeps its shader cache in, so it is left alone."
                : classification.Reason));

            // A directory is named in the plan because it is what a user would expect to go with the
            // cache. A loose file is protected all the same, but a note for each would bury the ones
            // that matter. A folder two layouts proved is read by each, and names it once.
            if (isDirectory && _reported.Add(path))
            {
                Notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{path}' alone: {Survivors[^1].Reason}"));
            }
        }

        return recognised;
    }

    /// <summary>
    /// The cache folders inside each game's folder in each of <paramref name="layout"/>'s folders of
    /// games. Only a folder named by a title id is entered, and a link among them is declined.
    /// </summary>
    private List<EmulatorCacheFolder> GameCacheFolders(EmulatorLayout layout, string root, CancellationToken ct)
    {
        var folders = new List<EmulatorCacheFolder>();

        foreach (var games in layout.GameCachesIn(root))
        {
            ct.ThrowIfCancellationRequested();

            if (DerivedPath.FirstObstacleBetween(root, games.GamesFolder) is { } obstacle)
            {
                Obstructed(obstacle);
                continue;
            }

            var listing = ChildDirectories.Under(games.GamesFolder, SwitchTitleId.IsMatch);

            if (listing.Unreadable)
            {
                Unlisted(games.GamesFolder);
                continue;
            }

            Survivors.Add((games.GamesFolder, $"{layout.Name}'s folder of games. Only caches inside each game's folder are removed."));

            foreach (var link in listing.Links)
            {
                DeclineLink(LongPath.Display(link.FullName));
            }

            foreach (var entry in listing.Directories)
            {
                var game = LongPath.Display(entry.FullName);

                _gameFolders.Add(game);
                Survivors.Add((game, $"One game's folder in {layout.Name}. Only caches inside it are removed."));
                Survivors.AddRange(games.ProtectedNames.Select(p => (Path.Combine(game, p.Name), p.Reason)));
                folders.AddRange(games.CacheFoldersIn(game));
            }
        }

        return folders;
    }

    /// <summary>A folder Windows said is there and then would not list.</summary>
    private void Unlisted(string path)
    {
        if (!_reported.Add(path))
        {
            return;
        }

        Notes.Add(UnreadableRoot.Note(path));
        Survivors.Add((path, UnreadableRoot.UnreachedReason));
        Unreadable = true;
    }

    private void Obstructed(DerivedPathObstacle obstacle)
    {
        if (obstacle.IsLink)
        {
            DeclineLink(obstacle.Path);
        }
        else
        {
            Unreached(obstacle.Path);
        }
    }

    private void Unreached(string path)
    {
        if (!_reported.Add(path))
        {
            return;
        }

        Notes.Add(UnreadableRoot.UnreachedNote(path));
        Survivors.Add((path, UnreadableRoot.UnreachedReason));
        Unreadable = true;
    }

    private void DeclineLink(string path)
    {
        if (!_reported.Add(path))
        {
            return;
        }

        Declined.Add(path);
        Survivors.Add((path, CacheLevelWalk.LinkReason));
        Notes.Add(CacheLevelWalk.Note(path));
    }
}
