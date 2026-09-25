using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What a set of <see cref="TempMarkerPlace"/>s came to: what may go, what must stay, and what the
/// row has to say about either.
/// </summary>
/// <param name="Targets">What may be removed, each grouped under the tool that wrote it.</param>
/// <param name="Survivors">What §5.6 asserts is still standing afterwards, with the reason.</param>
/// <param name="Notes">What the row tells the user about entries it held back or could not read.</param>
/// <param name="HeldBack">
/// Recognised entries left alone because their tool is still using them. Explore refuses each of
/// them, because the plan protects it.
/// </param>
/// <param name="Recognised">
/// Every path a marker recognised or a place's owner owns, taken or not. What
/// <see cref="ClaimsIn"/> answers from.
/// </param>
/// <param name="OwnedPlaces">
/// Each tool's own folder that was examined, with the tool, so Explore can refuse in it what the plan
/// would not take.
/// </param>
/// <param name="Declined">
/// How many recognised entries were left alone, so a plan with no steps is not read as "already
/// clear" while something is still there.
/// </param>
/// <param name="Unreadable">Whether a place would not be listed, so the figures are short by an unknown amount.</param>
public sealed record TempMarkerFindings(
    IReadOnlyList<DeletionTarget> Targets,
    IReadOnlyList<(string Path, string Reason)> Survivors,
    IReadOnlyList<PlanNote> Notes,
    IReadOnlyList<string> HeldBack,
    IReadOnlyList<string> Recognised,
    IReadOnlyList<(string Directory, string Owner)> OwnedPlaces,
    int Declined,
    bool Unreadable)
{
    /// <summary>
    /// The entries directly inside <paramref name="folders"/> this survey speaks for: each one a
    /// marker recognised there, and the top of each owned place below one.
    ///
    /// <para>A place is claimed by its topmost entry, because the owning tool decides for the whole of
    /// it. Handing <c>Roslyn</c> back to the temporary-folder row because only its dead sessions are
    /// recognised would let that row take a live session on its age alone, which is the mistake the
    /// session check exists to prevent.</para>
    /// </summary>
    public IReadOnlyList<string> ClaimsIn(IReadOnlyList<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            var root = Path.TrimEndingDirectorySeparator(folder);
            var canonical = LongPath.Unaliased(root);

            foreach (var path in Recognised)
            {
                var relative = Path.GetRelativePath(canonical, LongPath.Unaliased(path));

                if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                {
                    continue;
                }

                var top = relative.Split(Path.DirectorySeparatorChar, 2)[0];
                claimed.Add(Path.Combine(root, top));
            }
        }

        return [.. claimed];
    }
}

/// <summary>
/// Examine <see cref="TempMarkerPlace"/>s: recognise entries by name, hold back those their tool is
/// still using, and name everything that must survive.
///
/// <para>Shared by every row that recognises a tool's leftovers in a temporary folder, because the
/// mechanics are the same and their getting it wrong is the same catastrophe. What each row
/// recognises, and at which tier, is that row's own.</para>
///
/// <para><b>The clean asks again.</b> Each rule an entry was offered under — its tool not running,
/// the tool's own word, no program working in the folder — is carried on the target as a
/// <see cref="TempMarkerCheck"/> and asked afresh immediately before the removal, because a preview
/// can sit on screen while a tool starts.</para>
/// </summary>
public static class TempMarkerSurvey
{
    /// <summary>Why a directory is held back where no program's working directory could be read.</summary>
    private const string CannotTell = "Deguffer could not tell whether a running program is using this";

    public static TempMarkerFindings Examine(
        IReadOnlyList<TempMarkerPlace> places,
        IProcessInspector inspector,
        ILiveTreeInspector liveTrees,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(liveTrees);

        var survey = new Survey(inspector);

        foreach (var place in places)
        {
            ct.ThrowIfCancellationRequested();
            survey.Visit(place, ct);
        }

        return survey.Finish(liveTrees, ct);
    }

    /// <summary>One examination's running state, kept together because every visit adds to all of it.</summary>
    private sealed class Survey(IProcessInspector inspector)
    {
        private readonly Dictionary<TempMarker, IReadOnlyList<IUseCheck>> _entryRules = [];
        private readonly List<(string Path, TempMarker Marker, DateTime? LastWritten, string Holder)> _candidates = [];
        private readonly List<(string Path, string Reason)> _survivors = [];
        private readonly List<PlanNote> _notes = [];
        private readonly List<string> _heldBack = [];
        private readonly List<string> _recognised = [];
        private readonly List<(string Directory, string Owner)> _owned = [];
        private readonly Dictionary<TempMarker, IReadOnlyList<string>> _running = [];
        private readonly Dictionary<TempMarker, int> _heldByProcess = [];
        private readonly Dictionary<string, bool> _reachable = new(StringComparer.OrdinalIgnoreCase);
        private int _declined;
        private bool _unreadable;

        public void Visit(TempMarkerPlace place, CancellationToken ct)
        {
            if (!Reachable(place))
            {
                return;
            }

            if (place.Owner is { } owner)
            {
                _recognised.Add(place.Directory);
                _owned.Add((place.Directory, owner));
                _survivors.Add((
                    place.Directory,
                    $"This is {owner}'s own folder, and it must survive — only what Deguffer recognises inside it is removed."));
            }

            if (FolderEntries.Of(place.Directory) is not { } entries)
            {
                _notes.Add(UnreadableRoot.Note(place.Directory));
                _unreadable = true;
                return;
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                var path = LongPath.Display(entry.FullName);
                var isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
                var marker = place.Markers.FirstOrDefault(m => m.Recognises(entry.Name, isDirectory));

                if (marker is null)
                {
                    // §5.2: in a folder a tool owns, what the tool's markers do not name is not
                    // Deguffer's to take, so it is asserted rather than merely omitted.
                    if (place.Owner is { } tool)
                    {
                        _survivors.Add((path, $"Not something Deguffer recognises as {tool}'s to remove, so it is left alone."));
                    }

                    continue;
                }

                _recognised.Add(path);

                if (marker.Keeps)
                {
                    _survivors.Add((path, marker.Reason));
                    continue;
                }

                // Never followed, never removed: what is on the far side was never classified.
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    _notes.Add(CacheLevelWalk.Note(path));
                    _survivors.Add((path, CacheLevelWalk.LinkReason));
                    _declined++;
                    continue;
                }

                Consider(path, marker, isDirectory ? DirectoryAge.Of(path, ct) : entry.LastWriteTimeUtc);
            }
        }

        public TempMarkerFindings Finish(ILiveTreeInspector liveTrees, CancellationToken ct)
        {
            foreach (var (marker, count) in _heldByProcess)
            {
                var running = _running[marker];

                _notes.Add(new PlanNote(
                    PlanNoteSeverity.Warning,
                    $"Left {count} {marker.Tool} {(count == 1 ? "entry" : "entries")} alone, because "
                    + $"{string.Join(", ", running)} {(running.Count == 1 ? "is" : "are")} running and may be "
                    + $"using {(count == 1 ? "it" : "them")}. Close {(running.Count == 1 ? "it" : "them")} and "
                    + "scan again to include them."));
            }

            // One pass over the process table for every directory candidate, however many there are.
            var directories = _candidates
                .Where(c => c.Marker.Kind is not TargetKind.File)
                .Select(c => new RecognisedBuildDirectory(c.Path, c.Holder))
                .ToList();

            var live = LiveTreeVeto.Apply(liveTrees, directories, lockFiles: [], ct, unknown: CannotTell);
            var stillUnused = live.Cleared
                .DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(c => c.Path, c => c.StillUnused, StringComparer.OrdinalIgnoreCase);
            var vetoed = new HashSet<string>(live.Vetoed.Select(v => v.Directory), StringComparer.OrdinalIgnoreCase);

            foreach (var held in live.Vetoed)
            {
                _survivors.Add((
                    held.Directory,
                    $"A running program is working in this right now ({string.Join("; ", held.Holders)}), so it is left alone."));
                _heldBack.Add(held.Directory);
                _declined++;
            }

            if (LiveTreeVeto.NoteFor(live.Vetoed, v => Path.GetFileName(Path.TrimEndingDirectorySeparator(v.Directory)))
                is { } busy)
            {
                _notes.Add(busy);
            }

            if (LiveTreeVeto.IncompleteNote(live.Complete, "Close the tools that wrote them before cleaning.") is { } incomplete)
            {
                _notes.Add(incomplete);
            }

            var targets = new List<DeletionTarget>();

            foreach (var (path, marker, lastWritten, _) in _candidates)
            {
                if (vetoed.Contains(path))
                {
                    continue;
                }

                // A folder emptied in place is written to again by its tool, so it stays and is asserted.
                if (marker.Kind is TargetKind.DirectoryContents)
                {
                    _survivors.Add((path, $"{marker.Tool} writes into this folder, so it stays and only what is inside it goes."));
                }

                // An unrecognised live check leaves nothing to decide on: with the process table
                // unread, a directory is held back rather than offered.
                if (!live.Complete && marker.Kind is not TargetKind.File)
                {
                    _survivors.Add((path, $"{CannotTell}, so it is left alone."));
                    _heldBack.Add(path);
                    _declined++;
                    continue;
                }

                targets.Add(new DeletionTarget(
                    path,
                    marker.Reason,
                    lastWritten,
                    marker.Kind,
                    Group: marker.Tool,
                    UseCheck: Recheck(marker, stillUnused.GetValueOrDefault(path))));
            }

            return new TempMarkerFindings(
                targets,
                [.. _survivors.DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase)],
                _notes,
                _heldBack,
                _recognised,
                _owned,
                _declined,
                _unreadable);
        }

        /// <summary>
        /// Hold back an entry its tool says is live or may be using, or keep it as a candidate.
        /// </summary>
        private void Consider(string path, TempMarker marker, DateTime? lastWritten)
        {
            var running = Running(marker);

            if (running.Count > 0)
            {
                _survivors.Add((path, $"{string.Join(", ", running)} is running and may be using this, so it is left alone."));
                _heldBack.Add(path);
                _heldByProcess[marker] = _heldByProcess.GetValueOrDefault(marker) + 1;
                _declined++;
                return;
            }

            if (marker.InUse is { } inUse && inUse(Path.GetFileName(path)))
            {
                _survivors.Add((path, $"Left alone because {marker.InUseReason}."));
                _heldBack.Add(path);
                _declined++;
                return;
            }

            _candidates.Add((path, marker, lastWritten, Path.GetDirectoryName(path) ?? path));
        }

        /// <summary>
        /// The rules <see cref="Consider"/> and <see cref="Finish"/> offered <paramref name="path"/>
        /// under, in their order, for the clean to ask again. Null for an entry offered on none of them.
        ///
        /// <para>A directory carries the question the veto handed out with it, which holds it back where
        /// the answer is partial, because the survey refused every directory on a partial answer rather
        /// than offering it with a note.</para>
        /// </summary>
        /// <param name="directoryCheck">The veto's question, or null for a file, which the veto was never asked about.</param>
        private IUseCheck? Recheck(TempMarker marker, IUseCheck? directoryCheck)
        {
            if (!_entryRules.TryGetValue(marker, out var entryRules))
            {
                List<IUseCheck> rules = [];

                if (marker.HeldBy.Count > 0)
                {
                    rules.Add(new RunningProcessCheck(inspector, marker.HeldBy));
                }

                if (marker.InUse is { } inUse)
                {
                    rules.Add(new TempMarkerEntryCheck(inUse, marker.InUseReason));
                }

                entryRules = rules;
                _entryRules[marker] = entryRules;
            }

            IReadOnlyList<IUseCheck> all = directoryCheck is null ? entryRules : [.. entryRules, directoryCheck];

            return all.Count switch
            {
                0 => null,
                1 => all[0],
                _ => new TempMarkerCheck(all),
            };
        }

        /// <summary>Which of the marker's processes are running, asked once per marker per survey.</summary>
        private IReadOnlyList<string> Running(TempMarker marker)
        {
            if (!_running.TryGetValue(marker, out var running))
            {
                running = marker.HeldBy.Count == 0 ? [] : inspector.FindRunning(marker.HeldBy);
                _running[marker] = running;
            }

            return running;
        }

        /// <summary>
        /// Whether the place is there to examine, noting it where Windows would not say and declining
        /// it where it, or a directory it was reached through, is a link.
        /// </summary>
        private bool Reachable(TempMarkerPlace place)
        {
            foreach (var directory in Chain(place))
            {
                if (!_reachable.TryGetValue(directory, out var reachable))
                {
                    reachable = Judge(directory);
                    _reachable[directory] = reachable;
                }

                if (!reachable)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Whether one directory on the way to a place may be entered, saying why not where it may
        /// not. Asked once per directory per survey, because several places share the folders above
        /// them and a link there is one fact, not one per place below it.
        /// </summary>
        private bool Judge(string directory)
        {
            switch (LongPath.ProbeDirectory(directory, out var isLink))
            {
                case PathPresence.Absent:
                    return false;

                case PathPresence.Refused:
                    _notes.Add(UnreadableRoot.UnreachedNote(directory));
                    _unreadable = true;
                    return false;
            }

            if (isLink is true)
            {
                _notes.Add(CacheLevelWalk.Note(directory));
                _survivors.Add((directory, CacheLevelWalk.LinkReason));
                _recognised.Add(directory);
                _declined++;
                return false;
            }

            return true;
        }

        /// <summary>
        /// The directories from just below <see cref="TempMarkerPlace.Below"/> down to the place,
        /// outermost first, or the place alone where it was reached directly.
        /// </summary>
        private static IEnumerable<string> Chain(TempMarkerPlace place)
        {
            if (place.Below is not { } below)
            {
                return [place.Directory];
            }

            var chain = new List<string>();

            for (var directory = place.Directory;
                 directory is not null && !directory.Equals(below, StringComparison.OrdinalIgnoreCase)
                     && LongPath.Contains(below, directory);
                 directory = Path.GetDirectoryName(directory))
            {
                chain.Add(directory);
            }

            chain.Reverse();
            return chain;
        }
    }
}
