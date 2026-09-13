namespace Deguffer.Core.Safety;

/// <inheritdoc />
public sealed class LiveTreeInspector : ILiveTreeInspector
{
    public static readonly LiveTreeInspector Default = new();

    private readonly Lock _gate = new();
    private ProcessTable? _snapshot;

    public LiveTreeFindings FindLive(IReadOnlyList<LiveTreeQuery> candidates, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return LiveTreeFindings.Nothing;
        }

        var table = Snapshot(ct);
        var live = new List<LiveTree>();
        var complete = table.CurrentDirectoriesReadable;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var holders = new List<string>();

            foreach (var process in table.Processes)
            {
                if (process.ImagePath is { } image && LongPath.Contains(candidate.Directory, image))
                {
                    Add(holders, $"{process.Name} is running from inside it");
                }

                if (process.CurrentDirectory is { } working && LongPath.Contains(candidate.Project, working))
                {
                    Add(holders, $"{process.Name} is working in {Path.GetFileName(candidate.Project)}");
                }
            }

            var locks = ExistingLockFiles(candidate);

            if (locks.Count > 0)
            {
                var held = RestartManager.Query(locks, ct);

                if (!held.Answered)
                {
                    complete = false;
                }

                foreach (var holder in held.Holders)
                {
                    Add(holders, $"{holder} has it open");
                }
            }

            if (holders.Count > 0)
            {
                live.Add(new LiveTree(candidate.Directory, holders));
            }
        }

        return new LiveTreeFindings(live, complete);
    }

    public LiveTreeFindings FindOccupiedDirectories(CancellationToken ct = default)
    {
        var table = Snapshot(ct);

        // Keyed by the directory, because one program is several processes and a browser leaves
        // four of them in one folder. Four rows naming the same folder would be read as four folders.
        var holders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in table.Processes)
        {
            ct.ThrowIfCancellationRequested();

            // The executable's own folder rather than the executable, so that both signals ask the
            // same question of the same kind of path: which directory is this program in? Without
            // it a caller would need different rules for the boundary case, and the one that reads
            // a working directory would be the one that got it wrong.
            Record(
                holders,
                process.ImagePath is { } image ? Path.GetDirectoryName(image) : null,
                $"{process.Name} is running from inside it");

            Record(holders, process.CurrentDirectory, $"{process.Name} is working in it");
        }

        return Findings(holders, table.CurrentDirectoriesReadable);
    }

    /// <summary>
    /// <see cref="FindOccupiedDirectories"/> narrowed to the immediate children of
    /// <paramref name="directories"/>, so the two can never disagree about where a program is.
    /// </summary>
    public LiveTreeFindings FindLiveChildren(IReadOnlyList<string> directories, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(directories);

        if (directories.Count == 0)
        {
            return LiveTreeFindings.Nothing;
        }

        var occupied = FindOccupiedDirectories(ct);

        // Keyed by the child, because programs in two folders below one scratch entry are both
        // using that entry, and two rows naming it would be read as two entries.
        var holders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var place in occupied.Live)
        {
            if (ChildHolding(directories, place.Directory) is not { } child)
            {
                continue;
            }

            foreach (var holder in place.Holders)
            {
                Record(holders, child, holder);
            }
        }

        return Findings(holders, occupied.Complete);
    }

    /// <summary>
    /// Adds <paramref name="holder"/> under <paramref name="directory"/>, compared without a
    /// trailing separator: a working directory is read with one and an executable's folder without,
    /// and the same folder must not become two entries.
    /// </summary>
    private static void Record(Dictionary<string, List<string>> holders, string? directory, string holder)
    {
        if (directory is null)
        {
            return;
        }

        var key = Path.TrimEndingDirectorySeparator(directory);

        if (!holders.TryGetValue(key, out var found))
        {
            holders[key] = found = [];
        }

        Add(found, holder);
    }

    private static LiveTreeFindings Findings(Dictionary<string, List<string>> holders, bool complete) =>
        new([.. holders.Select(entry => new LiveTree(entry.Key, entry.Value))], complete);

    /// <summary>
    /// The immediate child of one of <paramref name="directories"/> that <paramref name="inside"/>
    /// is at or below, or null where it is below none of them.
    ///
    /// <para>Null for a directory that <em>is</em> one of them, which is a program running from a
    /// scratch folder's top level or sitting in it. There is no child to spare in that case, and
    /// the folder itself is never removed — so the honest answer is that this evidence names
    /// nothing, rather than the whole folder.</para>
    /// </summary>
    private static string? ChildHolding(IReadOnlyList<string> directories, string inside)
    {
        foreach (var directory in directories)
        {
            if (!LongPath.Contains(directory, inside)
                || Path.TrimEndingDirectorySeparator(inside).Length
                    <= Path.TrimEndingDirectorySeparator(directory).Length)
            {
                continue;
            }

            // The first segment below the directory, however deep the path runs. Taken as a
            // relative path rather than by string offset so that both separators and a trailing one
            // are the framework's problem rather than three off-by-one risks here.
            var relative = Path.GetRelativePath(directory, inside);
            var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

            return Path.Combine(directory, separator < 0 ? relative : relative[..separator]);
        }

        return null;
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _snapshot = null;
        }
    }

    private ProcessTable Snapshot(CancellationToken ct)
    {
        lock (_gate)
        {
            return _snapshot ??= Filtered(RunningProcessTable.Read(ct));
        }
    }

    /// <summary>
    /// The table without this process.
    ///
    /// Deguffer is normally started from somewhere inside a developer's own folders, and on this
    /// repository it is started from inside the very source tree it is asked to look at. Left in,
    /// it would report every project below its own working directory as busy — with itself.
    ///
    /// <para><b>By process id, which is the only identity that is one process.</b> The obvious
    /// alternative is the image path, and it reads as the wider and therefore safer rule — no
    /// Deguffer is evidence about a developer's tree, not just this one. It is not safer, because
    /// the image path is a fact about how the application is <em>hosted</em> rather than about the
    /// application. It is only Deguffer's own executable while the app ships self-contained, which
    /// is a property of <c>Deguffer.App.csproj</c>; framework-dependent, or started through a shared
    /// host, <see cref="Environment.ProcessPath"/> is <c>dotnet.exe</c> and every other
    /// <c>dotnet.exe</c> on the machine matches it — a build in flight included, which is precisely
    /// what the veto exists to catch. A safety filter must not be one edit to an unrelated project
    /// file away from disarming itself, and a process id cannot be reached from a build
    /// setting.</para>
    ///
    /// <para>What the narrower rule gives up is a second Deguffer running from inside the same
    /// source tree, which then vetoes it. That is the over-reporting direction: the user is told a
    /// directory looks busy and can look, where the wider rule's failure hands a live project to a
    /// deletion.</para>
    /// </summary>
    internal static ProcessTable Filtered(ProcessTable table) => table with
    {
        Processes = [.. table.Processes.Where(p => p.Id != Environment.ProcessId)],
    };

    /// <summary>
    /// The declared lock files that are actually on disk.
    ///
    /// Filtered rather than passed wholesale because a lock file's absence is the ordinary case —
    /// Unity writes <c>UnityLockfile</c> when the editor opens the project and removes it when the
    /// editor closes — and there is no point paying for a Restart Manager session to be told that a
    /// file which is not there is not open.
    ///
    /// <b>Presence alone is not the test.</b> A lock file left behind by a crashed editor is still
    /// on disk, so it is whether something holds it <em>open</em> that answers the question.
    /// </summary>
    private static IReadOnlyList<string> ExistingLockFiles(LiveTreeQuery candidate)
    {
        if (candidate.LockFileNames.Count == 0)
        {
            return [];
        }

        var present = new List<string>(candidate.LockFileNames.Count);

        foreach (var name in candidate.LockFileNames)
        {
            var path = Path.Combine(candidate.Directory, name);

            if (LongPath.FileExists(path))
            {
                present.Add(path);
            }
        }

        return present;
    }

    private static void Add(List<string> holders, string holder)
    {
        // One editor is several processes, and several of them can answer for the same directory.
        if (!holders.Contains(holder, StringComparer.Ordinal))
        {
            holders.Add(holder);
        }
    }
}
