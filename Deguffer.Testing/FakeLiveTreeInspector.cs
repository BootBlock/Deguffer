using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// Declares which directories are "in use", so a provider's veto is provable with no editor, no
/// build and no toolchain installed.
///
/// The real inspector is exercised against real live trees in <c>LiveTreeInspectorTests</c> —
/// nothing about the Restart Manager or the process table can be established with a fake. What this
/// proves is the other half: that a provider given a live directory refuses to target it, and that
/// it says so.
///
/// <para><b>Answers by the real inspector's contract, snapshot included.</b> The programs declared
/// with <see cref="WithProgram"/>, and whether the table could be read, stand for the process table,
/// which a query answers from a snapshot taken by the first query after <see cref="Invalidate"/>. A
/// change declared after it is not seen until <see cref="Invalidate"/>, or until
/// <see cref="DiscardOlderThan"/> finds the snapshot older than its bound, measured on a clock only
/// <see cref="PassTheReadingLimit"/> moves. A
/// fake that answered live would let a check at the clean stop reading the machine again with no
/// test going red. A file declared with <see cref="WithHeldFile"/> is answered live, as the real
/// inspector asks the Restart Manager on every query.</para>
/// </summary>
public sealed class FakeLiveTreeInspector : ILiveTreeInspector
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _live;
    private readonly HashSet<string> _unanswered = new(StringComparer.OrdinalIgnoreCase);
    private bool _complete;
    private readonly List<RunningProgram> _programs = [];
    private readonly List<TimeSpan> _ageLimits = [];
    private readonly Dictionary<string, string> _heldFiles = new(StringComparer.OrdinalIgnoreCase);
    private IVolumeInventory _volumes = new FakeVolumeInventory();
    private LiveTreeMatch? _match;
    private TimedReading<Table>? _snapshot;

    public FakeLiveTreeInspector(bool complete, params string[] live)
    {
        _live = new HashSet<string>(live, StringComparer.OrdinalIgnoreCase);
        _complete = complete;
    }

    public FakeLiveTreeInspector(params string[] live) : this(complete: true, live) { }

    public static FakeLiveTreeInspector NothingLive => new();

    /// <summary>Nothing found, and nothing established either — the "could not tell" case.</summary>
    public static FakeLiveTreeInspector CannotTell => new(complete: false);

    public int InvalidateCount { get; private set; }

    /// <summary>
    /// What a snapshot's age is measured by. It moves only by <see cref="PassTheReadingLimit"/>, so a
    /// test that changes the machine between the preview and the clean says the preview stayed on
    /// screen past the limit.
    /// </summary>
    private readonly ManualTimeProvider _clock = new();

    /// <summary>
    /// Move the snapshot's clock past <see cref="IUseCheck.ReadingLimit"/>, as a preview left on screen
    /// longer than that, or a command that runs longer, moves the real one. A check asked after it
    /// reads the machine again.
    /// </summary>
    public FakeLiveTreeInspector PassTheReadingLimit()
    {
        _clock.Advance(IUseCheck.ReadingLimit + TimeSpan.FromTicks(1));
        return this;
    }

    /// <summary>The age each <see cref="DiscardOlderThan"/> was asked with, in order.</summary>
    public IReadOnlyList<TimeSpan> AgeLimits => _ageLimits;

    /// <summary>
    /// Answer "could not tell" from the next snapshot, so a test can have the process table become
    /// unreadable between the preview and the clean.
    /// </summary>
    public FakeLiveTreeInspector CannotTellFromNow()
    {
        _complete = false;
        return this;
    }

    /// <summary>
    /// Answer "could not tell" about <paramref name="directories"/> alone, as the real inspector does for a
    /// lock file Windows would not describe or a folder it could not put in canonical form, while the
    /// process table is read in full.
    /// </summary>
    public FakeLiveTreeInspector WithUnanswered(params string[] directories)
    {
        _unanswered.UnionWith(directories);
        return this;
    }

    /// <summary>What the provider asked about, so a test can assert the project folder was passed.</summary>
    public IReadOnlyList<LiveTreeQuery> Asked { get; private set; } = [];

    /// <summary>
    /// Pretend a program is running from <paramref name="executable"/>, working in
    /// <paramref name="workingDirectory"/>, or both.
    ///
    /// <para>Declared as a program rather than as a live directory, and answered on both sides by
    /// the real inspector's containment rule: <see cref="FindOccupiedDirectories"/> reports where it
    /// is, and <see cref="FindLive"/> counts it against a candidate whose directory holds the
    /// executable, whose project holds the working directory, or one of whose
    /// <see cref="LiveTreeQuery.Workspaces"/> is the working directory. A provider that finds candidates
    /// from the first and confirms them with the second is then tested on the pair, which is the
    /// only thing that tells a program in a project's build directory from one merely below the
    /// project.</para>
    ///
    /// <para><paramref name="arguments"/> are the full paths it was started with, which
    /// <see cref="FindLiveChildren"/> alone answers, as the real inspector does: the child of a
    /// scratch folder an argument names, or names a path inside.</para>
    ///
    /// <para><paramref name="started"/> is when it started, which only a candidate naming
    /// <see cref="LiveTreeQuery.Writers"/> asks, by the real inspector's rule. Null is a start time
    /// Windows would not give.</para>
    /// </summary>
    public FakeLiveTreeInspector WithProgram(
        string name,
        string? executable = null,
        string? workingDirectory = null,
        IReadOnlyList<string>? arguments = null,
        DateTimeOffset? started = null)
    {
        _programs.Add(new RunningProgram(name, executable, workingDirectory, arguments ?? [], started));
        return this;
    }

    /// <summary>
    /// Follow each program's places and each candidate to every path <paramref name="volumes"/> says
    /// they are reachable at, as the real inspector does with the machine's. Without it, nothing is
    /// reachable anywhere but where it is named.
    /// </summary>
    public FakeLiveTreeInspector WithVolumes(IVolumeInventory volumes)
    {
        lock (_gate)
        {
            _volumes = volumes;
            _match = null;
        }

        return this;
    }

    /// <summary>
    /// Pretend <paramref name="holder"/> holds <paramref name="file"/> open, as the Restart Manager
    /// would report it: counted against a candidate that names the file among its lock files, and
    /// against nothing else, which is the real inspector's rule. A test can hold a file the plan never
    /// asked about, to prove a check asks about the files there are now rather than the ones there were.
    /// </summary>
    public FakeLiveTreeInspector WithHeldFile(string file, string holder)
    {
        _heldFiles[file] = holder;
        return this;
    }

    public LiveTreeFindings FindLive(IReadOnlyList<LiveTreeQuery> candidates, CancellationToken ct = default)
    {
        Asked = candidates;

        var (table, match) = Snapshot();
        var unknown = new List<string>();
        ReachedProgram[] programs =
            [.. table.Programs.Select(p => match.Program(p.Name, p.Executable, p.WorkingDirectory, p.Started))];

        return Answer(
            table,
            [.. candidates
                .Select(c => new LiveTree(c.Directory, HoldersOf(c, match, programs, unknown)))
                .Where(tree => tree.Holders.Count > 0)],
            [.. candidates.Select(c => c.Directory)],
            unknown);
    }

    public LiveTreeFindings FindOccupiedDirectories(CancellationToken ct = default) =>
        Occupied(Snapshot().Table);

    private static LiveTreeFindings Occupied(Table table) =>
        new(
            [.. table.Programs
                .SelectMany(program => new (string? Directory, string Holder)[]
                {
                    (program.Executable is { } executable ? Path.GetDirectoryName(executable) : null,
                        $"{program.Name} is running from inside it"),
                    (program.WorkingDirectory, $"{program.Name} is working in it"),
                })
                .Where(place => place.Directory is not null)
                .GroupBy(place => place.Directory!, StringComparer.OrdinalIgnoreCase)
                .Select(group => new LiveTree(group.Key, [.. group.Select(place => place.Holder)]))],
            table.Complete);

    /// <summary>
    /// What makes <paramref name="candidate"/> live, adding it to <paramref name="unknown"/> where a
    /// writer's start or its last write could not be compared, as the real inspector does.
    /// </summary>
    private List<string> HoldersOf(
        LiveTreeQuery candidate,
        LiveTreeMatch match,
        ReachedProgram[] programs,
        List<string> unknown)
    {
        var holders = new List<string>();

        if (_live.Contains(candidate.Directory))
        {
            holders.Add("a test says something is using it");
        }

        foreach (var name in candidate.LockFileNames)
        {
            // Relative to the directory, or taken as it stands where it is a full path, as the real
            // inspector's Path.Combine does.
            if (_heldFiles.TryGetValue(Path.Combine(candidate.Directory, name), out var holder))
            {
                holders.Add($"{holder} has it open");
            }
        }

        var reached = match.Candidate(candidate);

        foreach (var program in programs)
        {
            LiveTreeMatch.AddHolders(reached, program, holders);

            if (!LiveTreeMatch.AddWriter(reached, program, holders)
                && !unknown.Contains(candidate.Directory, StringComparer.OrdinalIgnoreCase))
            {
                unknown.Add(candidate.Directory);
            }
        }

        return holders;
    }

    /// <summary>
    /// The immediate children of <paramref name="directories"/> that are declared live, or that hold
    /// a place a program added with <see cref="WithProgram"/> runs from, works in or was started
    /// with, which is how the real inspector builds this answer.
    ///
    /// <para>Immediate, and never the root itself, because that is the contract
    /// <see cref="ILiveTreeInspector.FindLiveChildren"/> keeps: it names the child a plan can spare,
    /// and it answers nothing for a program sitting in the folder itself. A fake that reported more
    /// than the real one ever can would let a provider pass a test against behaviour it will never
    /// see.</para>
    /// </summary>
    public LiveTreeFindings FindLiveChildren(
        IReadOnlyList<string> directories,
        CancellationToken ct = default)
    {
        var (table, match) = Snapshot();

        return Answer(
            table,
            [
                .. _live
                    .Where(child => directories.Any(root => IsImmediateChild(root, child)))
                    .Select(child => new LiveTree(child, ["a test says something is using it"])),
                .. Occupied(table).Live
                    .Select(place => (Child: match.ChildHolding(directories, place.Directory), place.Holders))
                    .Where(found => found.Child is not null)
                    .Select(found => new LiveTree(found.Child!, found.Holders)),
                .. table.Programs
                    .SelectMany(program => program.Arguments
                        .Select(argument => match.ChildHolding(directories, argument))
                        .OfType<string>()
                        .Select(child => new LiveTree(child, [$"{program.Name} was started with it"]))),
            ],
            directories);
    }

    /// <summary>
    /// What the real inspector answers: nothing whole where the table could not be read, and otherwise
    /// a whole answer but for the directories asked about that a test made unanswerable, or whose
    /// writers' times could not be compared (<paramref name="unknown"/>).
    /// </summary>
    private LiveTreeFindings Answer(
        Table table,
        IReadOnlyList<LiveTree> live,
        IReadOnlyList<string> asked,
        IReadOnlyList<string>? unknown = null) =>
        table.Complete
            ? LiveTreeFindings.Partly(
                live,
                [.. asked.Where(directory => _unanswered.Contains(directory)
                    || (unknown?.Contains(directory, StringComparer.OrdinalIgnoreCase) ?? false))])
            : new LiveTreeFindings(live, Complete: false);

    private static bool IsImmediateChild(string root, string child) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(child)) is { } parent
        && parent.Equals(
            Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);

    public ReachedFolder Reach(string path) => Match.Reach(path);

    public void Invalidate()
    {
        lock (_gate)
        {
            InvalidateCount++;
            _snapshot?.Forget();
            _match = null;
        }
    }

    /// <summary>Recorded, and acted on as the real inspector acts on its own.</summary>
    public void DiscardOlderThan(TimeSpan maxAge)
    {
        lock (_gate)
        {
            _ageLimits.Add(maxAge);

            // The rule that matches the table goes with it, as the real inspector drops both.
            if (_snapshot?.ForgetOlderThan(maxAge) is true)
            {
                _match = null;
            }
        }
    }

    /// <summary>The rule that matches the table, kept until the snapshot is discarded, as the real one is.</summary>
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

    /// <summary>
    /// The declared table as the snapshot held saw it, or as it is now where none is held, and the rule
    /// it is matched by.
    /// </summary>
    private (Table Table, LiveTreeMatch Match) Snapshot()
    {
        lock (_gate)
        {
            var table = (_snapshot ??= new(_clock)).Get(() => new Table([.. _programs], _complete));
            return (table, Match);
        }
    }

    private sealed record Table(IReadOnlyList<RunningProgram> Programs, bool Complete);

    private sealed record RunningProgram(
        string Name,
        string? Executable,
        string? WorkingDirectory,
        IReadOnlyList<string> Arguments,
        DateTimeOffset? Started);
}
