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
    /// The folder, or null where <see cref="ConfigDirectoryVariable"/> names one Deguffer will not treat
    /// as Claude Code's. <see cref="WhyUnusable"/> says why.
    /// </summary>
    public static string? Resolve(IUserEnvironment environment, ISystemDirectories system) =>
        Examine(environment, system).Home;

    /// <summary>
    /// Why <see cref="Resolve"/> has no folder, as whole sentences, or null where it has one. A provider
    /// says it before it says what it is leaving alone.
    /// </summary>
    public static string? WhyUnusable(IUserEnvironment environment, ISystemDirectories system) =>
        Examine(environment, system).Why;

    /// <summary>
    /// Two ways for <see cref="ConfigDirectoryVariable"/> to be no answer.
    ///
    /// <list type="bullet">
    /// <item>A relative value resolves against the working directory of whichever process reads it,
    /// which Deguffer does not share, so there is no correct reading of it — the reasoning
    /// <see cref="CargoCacheProvider.ResolveHome"/> gives for <c>CARGO_HOME</c>.</item>
    /// <item>A value naming somewhere <see cref="ConfiguredFolder"/> refuses — the profile, a drive
    /// root, one of the account's own folders — would have every entry there asserted as a Claude
    /// Code survivor and refused in Explore as Claude Code's own, and a removal there by any other
    /// row would read as a failure of §5.6.</item>
    /// </list>
    /// </summary>
    private static (string? Home, string? Why) Examine(IUserEnvironment environment, ISystemDirectories system)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(system);

        if (ConfiguredValue(environment) is not { } configured)
        {
            return (Path.Combine(environment.UserProfile, DefaultDirectoryName), null);
        }

        if (LongPath.Configured(configured) is not { } folder)
        {
            return (null, $"{ConfigDirectoryVariable} is set to '{configured}', which is not a full path. "
                + "Deguffer cannot tell which folder that means.");
        }

        return ConfiguredFolder.WhyNotOwned(folder, environment, system, TempRoots.Resolve(environment, system).AccountFolders)
            is { } declined
                ? (null, $"{ConfigDirectoryVariable} is set to '{configured}', and Deguffer will not treat that as "
                    + $"Claude Code's folder: {declined}")
                : (folder, null);
    }

    /// <summary>
    /// What <see cref="ConfigDirectoryVariable"/> holds, trimmed, or null where it is unset. Named as it
    /// was set in the sentence about one Deguffer could not use.
    /// </summary>
    private static string? ConfiguredValue(IUserEnvironment environment) =>
        environment.GetEnvironmentVariable(ConfigDirectoryVariable)?.Trim() is { Length: > 0 } value
            ? value
            : null;

    /// <summary>
    /// The first place on the way down from the folder to <paramref name="folder"/> inside it that stops a
    /// walk, <paramref name="folder"/> included, or null where nothing does. A provider's plan, its survey
    /// and its declaration all ask this, so none of them can read a refusal as absence while another
    /// names it.
    ///
    /// <para>The folder first, then everything below it, and all of it before <paramref name="folder"/>
    /// is probed for. Probing for that resolves through the folder, so a link there that Windows declines
    /// to follow would leave <paramref name="folder"/> reading as unreachable and the link — which
    /// Deguffer can see perfectly well — never named.</para>
    /// </summary>
    internal static DerivedPathObstacle? FirstObstacle(string home, string folder)
    {
        switch (LongPath.ProbeDirectory(home, out var isLink))
        {
            case PathPresence.Refused:
                return new DerivedPathObstacle(home, IsLink: false);

            case PathPresence.Present when isLink is true:
                return new DerivedPathObstacle(home, IsLink: true);
        }

        return DerivedPath.FirstObstacleBetween(home, folder);
    }

    /// <summary>Whether <paramref name="name"/> is shaped like one of Claude Code's session ids.</summary>
    public static bool IsSessionId(string name) => SessionIdPattern().IsMatch(name);
}
