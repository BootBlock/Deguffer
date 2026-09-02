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
