namespace Deguffer.Core.Safety;

/// <inheritdoc />
public sealed class LiveTreeInspector : ILiveTreeInspector
{
    public static readonly LiveTreeInspector Default = new();

    private readonly IProcessTableCalls _calls;
    private readonly IVolumeInventory _volumes;
    private readonly Lock _gate = new();
    private ProcessTable? _snapshot;
    private LiveTreeMatch? _match;

    public LiveTreeInspector()
        : this(ProcessTableCalls.Instance, VolumeInventory.Current)
    {
    }

    /// <param name="volumes">Asked every other path a program's place and a directory are reachable at.</param>
    internal LiveTreeInspector(IProcessTableCalls calls, IVolumeInventory volumes)
    {
        _calls = calls;
        _volumes = volumes;
    }

    public LiveTreeFindings FindLive(IReadOnlyList<LiveTreeQuery> candidates, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return LiveTreeFindings.Nothing;
        }

        var (table, match) = Snapshot(ct);
        var live = new List<LiveTree>();
        var complete = table.ImagePathsReadable && table.CurrentDirectoriesReadable;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var holders = new List<string>();

            foreach (var process in table.Processes)
            {
                foreach (var holder in match.Holders(candidate, process.Name, process.ImagePath, process.CurrentDirectory))
                {
                    Add(holders, holder);
                }
            }

            var locks = ExistingLockFiles(candidate, out var anyRefused);

            if (anyRefused)
            {
                complete = false;
            }

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
        var (table, _) = Snapshot(ct);

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

        return Findings(holders, table.ImagePathsReadable && table.CurrentDirectoriesReadable);
    }

    /// <summary>
    /// <see cref="FindOccupiedDirectories"/> narrowed to the immediate children of
    /// <paramref name="directories"/>, so the two can never disagree about where a program is — and
    /// widened by the paths each program was started with, which only a scratch folder needs.
    /// </summary>
    public LiveTreeFindings FindLiveChildren(IReadOnlyList<string> directories, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(directories);

        if (directories.Count == 0)
        {
            return LiveTreeFindings.Nothing;
        }

        // Every path read from the process table is canonical already. A folder asked about that
        // cannot be made so is still compared as well as it can be, and the answer says it may have
        // missed something.
        var foldersWhole = directories.All(asked => LongPath.Canonical(asked) is not null);
        var occupied = FindOccupiedDirectories(ct);
        var (table, match) = Snapshot(ct);

        // Keyed by the child, because programs in two folders below one scratch entry are both
        // using that entry, and two rows naming it would be read as two entries.
        var holders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var place in occupied.Live)
        {
            if (match.ChildHolding(directories, place.Directory) is not { } child)
            {
                continue;
            }

            foreach (var holder in place.Holders)
            {
                Record(holders, child, holder);
            }
        }

        foreach (var process in table.Processes)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var argument in process.PathArguments)
            {
                Record(holders, match.ChildHolding(directories, argument), $"{process.Name} was started with it");
            }
        }

        return Findings(holders, occupied.Complete && table.CommandLinesReadable && foldersWhole);
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

    public ReachedFolder Reach(string path) => Match.Reach(path);

    public void Invalidate()
    {
        lock (_gate)
        {
            _snapshot = null;
            _match = null;
        }
    }

    /// <summary>
    /// The rule that matches the process table, which keeps where each path it follows is reachable
    /// until the reading is discarded.
    /// </summary>
    private LiveTreeMatch Match
    {
        get
        {
            lock (_gate)
            {
                return _match ??= new LiveTreeMatch(_volumes);
            }
        }
    }

    /// <summary>One reading of the process table, and the rule that matches it.</summary>
    private (ProcessTable Table, LiveTreeMatch Match) Snapshot(CancellationToken ct)
    {
        lock (_gate)
        {
            return (_snapshot ??= Filtered(RunningProcessTable.Read(_calls, ct)), _match ??= new LiveTreeMatch(_volumes));
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
    ///
    /// <para><b>A lock file Windows will not describe sets <paramref name="anyRefused"/>.</b> It may
    /// be there and held, so dropping it as absent would pass a project open in its editor as
    /// dormant. The caller reports the answer incomplete instead.</para>
    /// </summary>
    private static IReadOnlyList<string> ExistingLockFiles(LiveTreeQuery candidate, out bool anyRefused)
    {
        anyRefused = false;

        if (candidate.LockFileNames.Count == 0)
        {
            return [];
        }

        var present = new List<string>(candidate.LockFileNames.Count);

        foreach (var name in candidate.LockFileNames)
        {
            var path = Path.Combine(candidate.Directory, name);

            switch (LongPath.ProbeFile(path))
            {
                case PathPresence.Present:
                    present.Add(path);
                    break;

                case PathPresence.Refused:
                    anyRefused = true;
                    break;
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
