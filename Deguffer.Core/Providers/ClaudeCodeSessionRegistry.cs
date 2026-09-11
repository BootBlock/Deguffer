using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One session Claude Code lists as running, and when its process was created where that is known.</summary>
/// <param name="SessionId">
/// The session's id, which names its transcript, its sidecar and every folder Claude Code keeps for it.
/// </param>
/// <param name="StartedAt">
/// When the process running it was created, or null where that could not be established. Null is
/// never "long ago": <see cref="ClaudeCodeSessionList.Predates"/> reads it as a start that could be
/// the earliest of all.
/// </param>
public sealed record ClaudeCodeLiveSession(string SessionId, DateTimeOffset? StartedAt);

/// <summary>What Claude Code's list of running sessions said, and whether it could be read in full.</summary>
/// <param name="Live">Every session still running, or that nothing could establish had stopped.</param>
/// <param name="Complete">
/// False where the list, or any entry in it, could not be read. <see cref="LiveTreeFindings.Complete"/>
/// gives the reason this is carried rather than folded into <paramref name="Live"/>: "nothing is
/// running" and "we could not tell" lead to opposite decisions about a folder a running session is
/// still writing to. A caller refuses everything that depends on the list while this is false.
/// </param>
public sealed record ClaudeCodeSessionList(IReadOnlyList<ClaudeCodeLiveSession> Live, bool Complete)
{
    private readonly HashSet<string> _ids =
        new(Live.Select(session => session.SessionId), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the list names <paramref name="sessionId"/> as running.</summary>
    public bool Lists(string sessionId) => _ids.Contains(sessionId);

    /// <summary>
    /// Whether something last written at <paramref name="writtenUtc"/> predates every running session,
    /// so that none of them can have written it.
    ///
    /// <para>Asked of a file that names no session, where time is the only evidence: a process cannot
    /// write anything before it exists. It needs every running session's start, because one start
    /// nobody could read may be the earliest of them.</para>
    /// </summary>
    public bool Predates(DateTime writtenUtc) =>
        Complete && Live.All(session => session.StartedAt is { } started && writtenUtc < started.UtcDateTime);
}

/// <summary>
/// Claude Code's list of running sessions: one <c>&lt;process id&gt;.json</c> per process in
/// <see cref="ClaudeCodeHome.Sessions"/>, each naming the session that process is running.
///
/// <para><b>An entry is trusted only once its process has been asked about.</b> The list was measured
/// agreeing exactly with the running process table, twice. The messaging keys in the same folder were
/// not tidy, though, and fourteen of twenty-seven named a process that had ended. An entry is a claim
/// that a process was running when it was written, so every one is probed, and only
/// <see cref="ProcessState.NotRunning"/> takes a session off the list. An entry this machine cannot
/// ask about stays on it.</para>
///
/// <para><b>Only the process and the session are read.</b> An entry also records the project's folder
/// and the name the user gave the session, and neither is taken.</para>
///
/// <para><b>Every kind of process registers.</b> Claude Code's own reader of the list knows four kinds
/// of entry — an interactive session, a background one, a daemon and a daemon's worker — so the list
/// is not only the sessions somebody has open in a terminal or an editor.</para>
/// </summary>
public sealed partial class ClaudeCodeSessionRegistry
{
    /// <summary>
    /// Far past anything Claude Code writes, which is under a kilobyte, and small enough that a file
    /// which only shares the name is refused rather than read.
    /// </summary>
    private const int MaximumEntryBytes = 64 * 1024;

    private static readonly ClaudeCodeSessionList Unreadable = new([], Complete: false);

    [GeneratedRegex(@"\A(?<id>[0-9]{1,10})\.json\z", RegexOptions.CultureInvariant)]
    private static partial Regex EntryName();

    private readonly IUserEnvironment _environment;
    private readonly IProcessInspector _inspector;

    private ClaudeCodeSessionList? _list;

    public ClaudeCodeSessionRegistry(IUserEnvironment environment, IProcessInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(inspector);

        _environment = environment;
        _inspector = inspector;
    }

    /// <summary>
    /// The list, read once for the life of a planning pass (G4). Every entry's process is asked about
    /// when the list is read, so a session that ends during the pass is still listed — the direction
    /// that leaves its files alone.
    /// </summary>
    public ClaudeCodeSessionList Read(CancellationToken ct = default) => _list ??= ReadNow(ct);

    /// <summary>Drop the memoised list, so a session that started or ended is seen by the next pass.</summary>
    public void Invalidate() => _list = null;

    private ClaudeCodeSessionList ReadNow(CancellationToken ct)
    {
        if (ClaudeCodeHome.Resolve(_environment) is not { } home)
        {
            return Unreadable;
        }

        var directory = Path.Combine(home, ClaudeCodeHome.Sessions);

        // Not there is an answer: no session has registered. It is also how a version of Claude Code
        // that predates the list looks, which this cannot tell apart, and that is why no provider
        // relies on the list alone — each holds back what was written recently as well.
        if (!LongPath.DirectoryExists(directory))
        {
            return new ClaudeCodeSessionList([], Complete: true);
        }

        // Never read through, on the rule every walk in Core keeps: what a link points at was never
        // classified, and a list read from somewhere else describes somebody else's sessions.
        if (LongPath.IsReparsePoint(directory))
        {
            return Unreadable;
        }

        var live = new List<ClaudeCodeLiveSession>();

        try
        {
            foreach (var file in new DirectoryInfo(LongPath.Extended(directory)).EnumerateFiles())
            {
                ct.ThrowIfCancellationRequested();

                if (EntryName().Match(file.Name) is not { Success: true } match)
                {
                    continue;
                }

                // Named like an entry and naming no process that can exist. Claude Code never writes
                // one, so it is not an entry that can be read, and an entry that cannot be read may
                // be the one session still running.
                if (!int.TryParse(match.Groups["id"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
                    || processId < 1
                    || !TryRead(file.FullName, processId, out var running))
                {
                    return Unreadable;
                }

                if (running is not null)
                {
                    live.Add(running);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // §5.3 makes a refusal ordinary, and it is still not a list. Half a listing would drop the
            // entries after the one that failed.
            return Unreadable;
        }

        return new ClaudeCodeSessionList(live, Complete: true);
    }

    /// <returns>
    /// False where the entry could not be read. <paramref name="running"/> is then meaningless, and the
    /// whole list is incomplete.
    /// </returns>
    private bool TryRead(string path, int processId, out ClaudeCodeLiveSession? running)
    {
        running = null;

        using var document = BoundedJsonFile.Read(path, MaximumEntryBytes);

        if (document is null
            || BoundedJsonFile.StringProperty(document.RootElement, "sessionId") is not { Length: > 0 } sessionId)
        {
            return false;
        }

        // The file's name and its content have to agree about which process this is. An entry that
        // names another id is not one this can ask about.
        if (document.RootElement.TryGetProperty("pid", out var recorded)
            && !(recorded.ValueKind == JsonValueKind.Number
                 && recorded.TryGetInt32(out var recordedId)
                 && recordedId == processId))
        {
            return false;
        }

        if (!ClaudeCodeProcessRecord.IsThisMachine(
                BoundedJsonFile.StringProperty(document.RootElement, "pidDomain"), _environment))
        {
            // Another system's process, which nothing here can ask about. Listed as running with no
            // known start, which is the answer that protects everything it might have written.
            running = new ClaudeCodeLiveSession(sessionId, StartedAt: null);
            return true;
        }

        var recordedStart = ClaudeCodeProcessRecord.RecordedStart(document.RootElement);
        var liveness = _inspector.Probe(processId);

        running = ClaudeCodeProcessRecord.StateOf(liveness, recordedStart) switch
        {
            ProcessState.NotRunning => null,

            // The recorded start matched Windows' own to the tick where there was one. Where there was
            // not, Windows' answer is the only one there is, and it may be null.
            ProcessState.Running => new ClaudeCodeLiveSession(sessionId, recordedStart ?? liveness.StartedAt),

            // No start at all, not the recorded one. Undetermined includes a process created before
            // the record says, and a recorded start later than the real one would let a file the
            // session wrote read as older than the session.
            _ => new ClaudeCodeLiveSession(sessionId, StartedAt: null),
        };

        return true;
    }
}
