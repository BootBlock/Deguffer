using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where Claude Code keeps its state for this user, and the names it gives the things inside.
///
/// <para><b>Resolved the way Claude Code resolves it.</b> <see cref="ConfigDirectoryVariable"/> moves
/// the whole folder, and Claude Code's own documentation says settings and session history then go
/// wherever it points. Everything under it follows, the list of running sessions included. A
/// provider that read the default folder while Claude Code wrote somewhere else would find no running
/// session at all, and would take that as evidence that nothing is running.</para>
///
/// <para><b>Named exactly, and never matched by pattern.</b> <c>%USERPROFILE%\.claude.json</c> beside
/// the folder is Claude Code's own configuration and holds the account's identity, and
/// <c>%USERPROFILE%\.claude-swap-backup</c> belongs to a different program entirely and holds
/// encrypted credentials. A pattern such as <c>.claude*</c> would take both.</para>
/// </summary>
public static partial class ClaudeCodeHome
{
    /// <summary>Set by the user to move Claude Code's folder somewhere other than the profile.</summary>
    public const string ConfigDirectoryVariable = "CLAUDE_CONFIG_DIR";

    /// <summary>The folder's name in the profile, where <see cref="ConfigDirectoryVariable"/> is unset.</summary>
    public const string DefaultDirectoryName = ".claude";

    /// <summary>Conversation transcripts, one folder per project, with each project's memory beside them.</summary>
    public const string Projects = "projects";

    /// <summary>The list of running sessions, and the messaging key each running process publishes.</summary>
    public const string Sessions = "sessions";

    /// <summary>A folder per session for the environment its hooks set.</summary>
    public const string SessionEnvironments = "session-env";

    /// <summary>The shell environment Claude Code captured each time it started a shell.</summary>
    public const string ShellSnapshots = "shell-snapshots";

    /// <summary>Usage events that could not be sent, queued to be tried again.</summary>
    public const string Telemetry = "telemetry";

    /// <summary>The handshake file each connected editor writes so that Claude Code can find it.</summary>
    public const string Ide = "ide";

    /// <summary>A folder per session of the copies taken before each edit, so the session can be rewound.</summary>
    public const string FileHistory = "file-history";

    /// <summary>
    /// A session id as Claude Code writes one. The same pattern Claude Code checks its own names
    /// against, so a folder this accepts is one Claude Code would also take for a session's.
    /// </summary>
    [GeneratedRegex(
        @"\A[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();

    /// <summary>
    /// The folder, or null where <see cref="ConfigDirectoryVariable"/> holds something that is not a
    /// full path. A relative value resolves against the working directory of whichever process reads
    /// it, which Deguffer does not share, so there is no correct reading of it — the reasoning
    /// <see cref="CargoCacheProvider.ResolveHome"/> gives for <c>CARGO_HOME</c>.
    /// </summary>
    public static string? Resolve(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return ConfiguredValue(environment) is { } configured
            ? LongPath.Configured(configured)
            : Path.Combine(environment.UserProfile, DefaultDirectoryName);
    }

    /// <summary>
    /// What <see cref="ConfigDirectoryVariable"/> holds, trimmed, or null where it is unset. A
    /// provider names the value in the sentence it writes about one it could not use.
    /// </summary>
    public static string? ConfiguredValue(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return environment.GetEnvironmentVariable(ConfigDirectoryVariable)?.Trim() is { Length: > 0 } value
            ? value
            : null;
    }

    /// <summary>Whether <paramref name="name"/> is shaped like one of Claude Code's session ids.</summary>
    public static bool IsSessionId(string name) => SessionIdPattern().IsMatch(name);
}
