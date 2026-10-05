using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The snapshots Claude Code takes of a project so that it can show what each shell command changed: a
/// private Git folder per project in <c>%TEMP%\claude\bash-edit-diff</c>, holding an index of the project
/// and a copy of every file Git did not already have. 24.4 GB across 102 of them on the machine that
/// prompted this, all written in the two days before.
///
/// <para><b>A Claude Code process keeps one snapshot per project for as long as it runs, and its removal
/// fails.</b> Every command the process runs in the project reuses the snapshot, and the process removes
/// it when an error ends it or when the process exits. The removal at exit is given a fraction of a
/// second, and a removal that meets a file Windows will not release gives up and logs it, so whatever was
/// not removed in time is left. 75 of the 102 were half removed in exactly that way. Claude Code sweeps the
/// folder itself only for entries older than two days, which is why none is ever old enough for the
/// "Temporary files" row's age rule. §5.1 was asked and the answer is no: nothing in Claude Code removes
/// them on request.</para>
///
/// <para><b>Time is the evidence, because a snapshot cannot be tied to its process.</b> Its name begins
/// with a hash of the session the process was running when it took the snapshot, but a process keeps the
/// snapshot when it moves to another session, as <c>/clear</c> and <c>/resume</c> make it do, and the list
/// of running sessions then names only the new one. What does hold is that a process cannot make anything
/// before it exists. A snapshot created before every running session's process started was made by a
/// process that has ended (<see cref="ClaudeCodeSessionList.Predates"/>), and that needs every running
/// session's start. A list that cannot be read, or that Claude Code does not keep here at all
/// (<see cref="ClaudeCodeSessionList.Kept"/>), offers nothing on this evidence.</para>
///
/// <para><b>A snapshot Claude Code has begun to remove is offered whatever made it.</b> Claude Code writes
/// <c>HEAD</c> before it copies the project's <c>index</c> in, and forgets a snapshot before it removes
/// it, so an <c>index</c> with no <c>HEAD</c> beside it is a removal that failed part way. A process left
/// running for days accumulates these, and its start would otherwise keep every one. Where Windows will
/// not say whether either file is there, the snapshot is judged by time instead.</para>
///
/// <para><b>No week-long floor.</b> The other Claude Code rows hold back everything written in the last
/// <see cref="ClaudeCodeSessionRegistry.RecentWindow"/> as well, because a version of Claude Code older
/// than the list is invisible to it. Longer than Claude Code's own two-day sweep, that floor would offer
/// nothing here. What a process the list cannot see costs instead is bounded: a snapshot taken from under
/// it makes Claude Code's next diff in that project fail, and Claude Code then takes a new snapshot, or
/// after repeated failures stops showing diffs for that project until the session ends. It never
/// touches the project. Nothing of the user's is in a snapshot, which is why the row is Tier 1.</para>
///
/// <para><b>That residual is wider than one process where Claude Code runs with another folder.</b>
/// <c>CLAUDE_CONFIG_DIR</c> moves the list with the rest of Claude Code's folder, but not the snapshots, so
/// a session started with it set where Deguffer was not registers in a list Deguffer never reads. Every
/// snapshot such sessions are using can then be offered at once. The cost per snapshot is the same lost
/// diff.</para>
///
/// <para><b>A snapshot counts as made before a session only by <see cref="ClockTolerance"/>.</b> A creation
/// time can read earlier than the moment it records: the clock can be stepped back, and a temporary folder
/// on a FAT drive stores local time, which a change to or from summer time moves by an hour.</para>
///
/// <para><b>Only the snapshot folder is this row's.</b> The rest of Claude Code's temporary folder, each
/// session's scratch and command output, is left to the "Temporary files" row, which takes it on its age
/// rule as it did before this row existed. See <see cref="TempMarkerPlace.ClaimsOnlyItself"/>.</para>
/// </summary>
public sealed partial class ClaudeCodeCommandSnapshotProvider : TempMarkerProviderBase
{
    /// <summary>Claude Code's folder in each temporary folder.</summary>
    public const string TemporaryFolder = "claude";

    /// <summary>The folder inside <see cref="TemporaryFolder"/> Claude Code keeps the snapshots in.</summary>
    public const string SnapshotFolder = "bash-edit-diff";

    private const string Tool = "Claude Code";

    private const string Reason =
        "A snapshot Claude Code took to show what shell commands changed. It was made before every Claude Code "
        + "session now running started, or Claude Code had already begun to remove it, so no session Claude "
        + "Code lists reads it again.";

    /// <summary>How much earlier than every running session's start a snapshot must have been made.</summary>
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromHours(2);

    /// <summary>
    /// A hash of the session, a hash of the project, and eight random bytes in hexadecimal. <c>Bun.hash</c>
    /// returns a 64-bit integer, which Claude Code writes in decimal. Narrower than the pattern Claude
    /// Code checks its own names against, which allows letters in the first part that no hash it writes
    /// contains.
    /// </summary>
    [GeneratedRegex(
        @"\A[0-9]{1,20}-[0-9]{1,20}-[0-9a-f]{16}\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotName();

    private readonly ClaudeCodeSessionRegistry _sessions;

    /// <summary>
    /// What each snapshot's own files said, read once per planning pass (G5) so that the preview and the
    /// check it hands the clean decide on the same evidence.
    /// </summary>
    private readonly ConcurrentDictionary<string, SnapshotEvidence> _evidence = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="sessions">
    /// Claude Code's list of running sessions, shared with the other Claude Code rows so that one planning
    /// pass reads it, and asks about each process in it, once.
    /// </param>
    public ClaudeCodeCommandSnapshotProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        ILiveTreeInspector? liveTrees = null,
        ClaudeCodeSessionRegistry? sessions = null)
        : base(environment, runner, inspector, scanner, system, liveTrees)
    {
        _sessions = sessions ?? new ClaudeCodeSessionRegistry(Environment, Inspector, Machine);
    }

    public override string Id => "claude-code-command-snapshots";

    public override string Name => "Claude Code command snapshots";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "Nothing changes. Claude Code takes a new snapshot the next time it runs a command in a project, and "
        + "the snapshots a running session may still be using are left where they are.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Claude Code, Anthropic's coding agent",
        Publisher = "Anthropic",
        Purpose = "Claude Code keeps a snapshot of each project it runs shell commands in, in your temporary "
            + "folder, so that it can show what each command changed. It removes the snapshot when it exits, "
            + "but a removal that fails leaves the snapshot behind, and Claude Code clears those only once "
            + "they are two days old. A busy machine can collect tens of gigabytes of them.",
        Recommendation = "Deguffer offers the snapshots made before every Claude Code session now running "
            + "started, and those Claude Code had already begun to remove. Anything a running session may "
            + "be using is left alone, and the rest of Claude Code's temporary folder is left to the "
            + "Temporary files row.",
    };

    protected override string NothingLeftBehind => "Claude Code has left no command snapshots behind.";

    public override void InvalidateCaches()
    {
        _evidence.Clear();
        _sessions.Invalidate();
        base.InvalidateCaches();
    }

    protected override IReadOnlyList<TempMarkerPlace> PlacesIn(IReadOnlyList<string> accountFolders) =>
    [
        .. accountFolders.Select(folder =>
        {
            var snapshots = Path.Combine(folder, TemporaryFolder, SnapshotFolder);

            return new TempMarkerPlace(snapshots, [Snapshot(snapshots)], Tool, Below: folder, ClaimsOnlyItself: true);
        }),
    ];

    /// <summary>
    /// Why only what Claude Code began to remove is offered, where the list of running sessions could not
    /// be read or is not kept here.
    /// </summary>
    protected override IEnumerable<PlanNote> NotesFor(IReadOnlyList<string> accountFolders)
    {
        var list = _sessions.Read();

        var why = !list.Complete
            ? ClaudeCodeHome.WhyUnusable(Environment, Machine)
                ?? "Deguffer could not read Claude Code's list of running sessions."
            : !list.Kept
                ? "Claude Code keeps no list of running sessions here."
                : null;

        return why is null
            ? []
            :
            [
                new PlanNote(
                    PlanNoteSeverity.Warning,
                    why + " Without it Deguffer cannot tell which snapshots a running session may be using, so "
                    + "it offers only those Claude Code had already begun to remove."),
            ];
    }

    /// <summary>
    /// What is recognised in one snapshot folder. Built for the folder, because whether Claude Code has
    /// begun to remove a snapshot, and when it was made, are questions about the snapshot's own files.
    /// </summary>
    private TempMarker Snapshot(string snapshots) =>
        new(Tool, SnapshotName(), TargetKind.Directory, Reason)
        {
            InUse = (name, ct) => Evidence(Path.Combine(snapshots, name)).InUse(_sessions.Read(ct)),
            CheckAtClean = snapshot => Evidence(snapshot) switch
            {
                { RemovalBegun: true } => null,
                { MadeBy: { } madeBy } => ClaudeCodeSessionCheck.MadeBeforeEverySession(_sessions, madeBy),

                // The preview holds back a snapshot it cannot date, and reads the same evidence, so the
                // clean is never handed one.
                _ => throw new UnreachableException("A snapshot nobody could date was offered."),
            },
            InUseReason = "a Claude Code session that is running now may have made it",
        };

    private SnapshotEvidence Evidence(string snapshot) =>
        _evidence.GetOrAdd(snapshot, static path => new SnapshotEvidence(RemovalBegun(path), MadeBy(path)));

    /// <summary>
    /// Whether Claude Code has begun to remove the snapshot: its <c>index</c> is there and its <c>HEAD</c>
    /// is not. Either answer Windows would not give is not that evidence.
    /// </summary>
    private static bool RemovalBegun(string snapshot) =>
        LongPath.ProbeFile(Path.Combine(snapshot, "index")) is PathPresence.Present
        && LongPath.ProbeFile(Path.Combine(snapshot, "HEAD")) is PathPresence.Absent;

    /// <summary>
    /// The latest moment the process that made the snapshot can have been running by, or null where Windows
    /// would not say when the snapshot was made. Its creation rather than its last write, because a process
    /// keeps writing to its snapshot for as long as it runs and only the creation bounds when it started.
    /// </summary>
    private static DateTime? MadeBy(string snapshot)
    {
        try
        {
            return Directory.GetCreationTimeUtc(LongPath.Extended(snapshot)) + ClockTolerance;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <param name="RemovalBegun">Whether Claude Code has begun to remove the snapshot.</param>
    /// <param name="MadeBy">See <see cref="MadeBy(string)"/>.</param>
    private readonly record struct SnapshotEvidence(bool RemovalBegun, DateTime? MadeBy)
    {
        /// <summary>
        /// Whether a running session may be using the snapshot: Claude Code has not begun to remove it, and
        /// one of <paramref name="sessions"/> could have made it, or nothing can say that none did.
        /// </summary>
        public bool InUse(ClaudeCodeSessionList sessions) =>
            !RemovalBegun && !(sessions.Kept && MadeBy is { } madeBy && sessions.Predates(madeBy));
    }
}
