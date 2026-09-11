using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The files Claude Code's world writes that name a running process: the handshake file an editor
/// leaves for Claude Code to find it by, and the messaging key each Claude Code process publishes.
/// Each is offered once the process it names has been asked about and has ended, and never on its
/// age.
///
/// <para><b>They are here for the token as much as the space.</b> Both kinds hold a secret: a
/// handshake file carries the editor's connection token, and a key its process's peer token.
/// Measured, 203 of 217 handshake files named an editor that had closed, the oldest three months
/// earlier. The token is never read into anything Deguffer keeps, and never shown.</para>
///
/// <para><b>Only <see cref="ProcessState.NotRunning"/> offers one.</b> A running process keeps its
/// file, and so does one Deguffer could not ask about. A recycled id reads as running and keeps the
/// file too, which is the safe direction to be wrong in. A key records its process's creation time as
/// well, and that is used to tell a recycled id from the process itself.</para>
/// </summary>
internal static partial class ClaudeCodeProcessFiles
{
    /// <summary>Well past a handshake file, which lists the editor's open folders and nothing larger.</summary>
    private const int MaximumLockBytes = 64 * 1024;

    /// <summary>The limit Claude Code itself reads a messaging key to.</summary>
    private const int MaximumKeyBytes = 4096;

    private const string LocksFolderReason =
        "The folder editors write their handshake files into so that Claude Code can connect to them. It "
        + "stays: only the files of editors that have closed are removed.";

    private const string KeysFolderReason =
        "Claude Code's list of running sessions, and the messaging key each running process publishes. It "
        + "stays: only the keys of processes that have ended are removed.";

    private const string LockReason =
        "The handshake file of an editor that has closed. It holds that editor's connection token, and "
        + "nothing connects through it once the editor has gone.";

    private const string KeyReason =
        "The messaging key of a Claude Code process that has ended. It holds that process's token, and "
        + "Claude Code itself treats a key whose process has gone as unusable.";

    private const string RunningLockReason = "The editor that wrote this is still running.";

    private const string RunningKeyReason = "The Claude Code process that wrote this is still running.";

    private const string UnknownLockReason =
        "Deguffer could not tell whether the editor that wrote this is still running, so it is left alone.";

    private const string UnknownKeyReason =
        "Deguffer could not tell whether the Claude Code process that wrote this is still running, so it is "
        + "left alone.";

    private const string RegistryEntryReason =
        "An entry in Claude Code's list of running sessions. Deguffer reads the list to tell which sessions "
        + "are running, and never removes it.";

    [GeneratedRegex(@"\A(?<port>[0-9]{1,5})\.lock\z", RegexOptions.CultureInvariant)]
    private static partial Regex LockName();

    [GeneratedRegex(@"\A[0-9]{1,10}\.json\z", RegexOptions.CultureInvariant)]
    private static partial Regex RegistryEntryName();

    /// <summary>The pattern Claude Code checks its own keys' names against.</summary>
    [GeneratedRegex(@"\A(?<id>[0-9]{1,10})\.[0-9a-f]{64}\.key\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyName();

    /// <summary>The handshake files of editors that have closed.</summary>
    public static ClaudeCodeClassification EditorLocks(
        ClaudeCodeEvidence evidence,
        IProcessInspector inspector,
        CancellationToken ct)
    {
        var sorting = new ClaudeCodeClassificationBuilder();

        if (sorting.Open(Path.Combine(evidence.Home, ClaudeCodeHome.Ide), LocksFolderReason) is not { } entries)
        {
            return sorting.Build();
        }

        var running = 0;
        var unknown = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (!IsPlainFile(entry)
                || LockName().Match(entry.Name) is not { Success: true } match
                || !int.TryParse(match.Groups["port"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port is < 1 or > 65535)
            {
                sorting.Keep(path, ClaudeCodeClassificationBuilder.UnrecognisedReason);
                continue;
            }

            var state = EditorProcess(path) is { } processId
                ? inspector.Probe(processId).State
                : ProcessState.Undetermined;

            switch (state)
            {
                case ProcessState.NotRunning:
                    sorting.Offer(new DeletionTarget(
                        path, LockReason, entry.LastWriteTimeUtc, TargetKind.File, IsLeftover: true));
                    break;

                case ProcessState.Running:
                    sorting.Refuse(path, RunningLockReason);
                    running++;
                    break;

                default:
                    sorting.Refuse(path, UnknownLockReason);
                    unknown++;
                    break;
            }
        }

        NoteHeld(sorting, running, unknown, "handshake file", "handshake files", "editor");

        return sorting.Build();
    }

    /// <summary>The messaging keys of Claude Code processes that have ended.</summary>
    public static ClaudeCodeClassification MessagingKeys(
        ClaudeCodeEvidence evidence,
        IProcessInspector inspector,
        IUserEnvironment environment,
        CancellationToken ct)
    {
        var sorting = new ClaudeCodeClassificationBuilder();

        if (sorting.Open(Path.Combine(evidence.Home, ClaudeCodeHome.Sessions), KeysFolderReason) is not { } entries)
        {
            return sorting.Build();
        }

        var running = 0;
        var unknown = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (IsPlainFile(entry) && RegistryEntryName().IsMatch(entry.Name))
            {
                sorting.Keep(path, RegistryEntryReason);
                continue;
            }

            if (!IsPlainFile(entry)
                || KeyName().Match(entry.Name) is not { Success: true } match
                || !int.TryParse(match.Groups["id"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
                || processId < 1)
            {
                sorting.Keep(path, ClaudeCodeClassificationBuilder.UnrecognisedReason);
                continue;
            }

            switch (KeyProcessState(path, processId, inspector, environment))
            {
                case ProcessState.NotRunning:
                    sorting.Offer(new DeletionTarget(
                        path, KeyReason, entry.LastWriteTimeUtc, TargetKind.File, IsLeftover: true));
                    break;

                case ProcessState.Running:
                    sorting.Refuse(path, RunningKeyReason);
                    running++;
                    break;

                default:
                    sorting.Refuse(path, UnknownKeyReason);
                    unknown++;
                    break;
            }
        }

        NoteHeld(sorting, running, unknown, "messaging key", "messaging keys", "Claude Code process");

        return sorting.Build();
    }

    /// <summary>
    /// The Windows process an editor's handshake file names, or null where it names none this machine
    /// can ask about.
    ///
    /// <para><c>runningInWindows</c> has to say so. A handshake file an editor wrote from anywhere else
    /// records an id this machine's process table did not issue, and probing it would ask about
    /// whichever Windows process holds that number.</para>
    /// </summary>
    private static int? EditorProcess(string path)
    {
        using var document = BoundedJsonFile.Read(path, MaximumLockBytes);

        return document is not null
            && document.RootElement.TryGetProperty("runningInWindows", out var windows)
            && windows.ValueKind == JsonValueKind.True
            && document.RootElement.TryGetProperty("pid", out var pid)
            && pid.ValueKind == JsonValueKind.Number
            && pid.TryGetInt32(out var processId)
            && processId > 0
                ? processId
                : null;
    }

    /// <summary>
    /// Whether the process a messaging key names has ended. Undetermined where the key would not be
    /// read, or was written in another process namespace.
    /// </summary>
    private static ProcessState KeyProcessState(
        string path,
        int processId,
        IProcessInspector inspector,
        IUserEnvironment environment)
    {
        using var document = BoundedJsonFile.Read(path, MaximumKeyBytes);

        if (document is null
            || !ClaudeCodeProcessRecord.IsThisMachine(
                BoundedJsonFile.StringProperty(document.RootElement, "pidDomain"), environment))
        {
            return ProcessState.Undetermined;
        }

        return ClaudeCodeProcessRecord.StateOf(
            inspector.Probe(processId),
            ClaudeCodeProcessRecord.RecordedStart(document.RootElement));
    }

    private static bool IsPlainFile(FileSystemInfo entry) =>
        entry is FileInfo && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>
    /// One sentence each for what a running process kept and what nobody could ask about, rather than
    /// one per file: a machine with a dozen editor windows open has a dozen of them.
    /// </summary>
    private static void NoteHeld(
        ClaudeCodeClassificationBuilder sorting,
        int running,
        int unknown,
        string singular,
        string plural,
        string writer)
    {
        if (running > 0)
        {
            sorting.Note(
                PlanNoteSeverity.Information,
                $"Left {ClaudeCodeClassificationBuilder.Count(running, singular, plural)} alone: the {writer} "
                + $"that wrote {(running == 1 ? "it" : "each one")} is still running.");
        }

        if (unknown > 0)
        {
            sorting.Note(
                PlanNoteSeverity.Information,
                $"Left {ClaudeCodeClassificationBuilder.Count(unknown, singular, plural)} alone: Deguffer could "
                + $"not tell whether the {writer} that wrote {(unknown == 1 ? "it" : "each one")} is still running.");
        }
    }
}
