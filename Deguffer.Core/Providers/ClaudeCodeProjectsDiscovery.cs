using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>A folder named for a session, beside that session's transcript: the output Claude Code spilled out of it.</summary>
/// <param name="Path">The folder, in display form.</param>
/// <param name="SessionId">The session it is named for.</param>
/// <param name="IsLink">
/// Whether it is a junction or a symbolic link. Named rather than dropped, and never followed.
/// </param>
public sealed record ClaudeCodeSidecar(string Path, string SessionId, bool IsLink);

/// <summary>One project folder under <see cref="ClaudeCodeHome.Projects"/>, and what it holds.</summary>
/// <param name="Path">The folder, in display form. Never a target: the project's memory is inside it.</param>
/// <param name="Sidecars">
/// Every child folder named for a session, whether or not that session still has a transcript.
/// </param>
/// <param name="Others">
/// Everything else in the folder except the transcripts. The project's <c>memory</c> is among these,
/// and it is the reason a project folder is never a target.
/// </param>
public sealed record ClaudeCodeProjectFolder(
    string Path,
    IReadOnlyList<ClaudeCodeSidecar> Sidecars,
    IReadOnlyList<FileSystemInfo> Others);

/// <summary>What one walk over Claude Code's project folders found, with what it could not read beside it.</summary>
/// <param name="Folders">The project folders that were listed.</param>
/// <param name="TranscriptIds">
/// Every session with a transcript in any project folder, whichever folder that transcript is in.
/// </param>
/// <param name="Unreadable">Folders that refused to be listed, the projects folder itself included.</param>
/// <param name="Links">Project folders that are links, which are named and never listed through.</param>
/// <param name="Complete">
/// False where anything above could not be established. A session this walk found no transcript for
/// may then have one in a folder it did not see, so nothing may be called an orphan.
/// </param>
public sealed record ClaudeCodeProjects(
    IReadOnlyList<ClaudeCodeProjectFolder> Folders,
    IReadOnlySet<string> TranscriptIds,
    IReadOnlyList<string> Unreadable,
    IReadOnlyList<string> Links,
    bool Complete);

/// <summary>
/// One walk over Claude Code's project folders, for everything that has to know which sessions still
/// have a transcript.
///
/// <para><b>Only one level of each project folder is listed.</b> <c>projects</c> held 15,847 files on
/// the measured machine, nearly all of them inside the session folders, and which sessions have a
/// transcript is answered by the names beside those folders. Nothing below a session folder is
/// read here.</para>
///
/// <para><b>Which sessions have a transcript is answered across every project folder, not within
/// one.</b> Three session folders on the measured machine sat in a different project folder from
/// their own session's transcript. A set difference taken one project folder at a time would have
/// called their output an orphan.</para>
/// </summary>
public sealed class ClaudeCodeProjectsDiscovery
{
    private const string TranscriptExtension = ".jsonl";

    private static readonly IReadOnlySet<string> NoTranscripts = new HashSet<string>();

    private readonly IUserEnvironment _environment;

    private ClaudeCodeProjects? _projects;

    public ClaudeCodeProjectsDiscovery(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _environment = environment;
    }

    /// <summary>
    /// The walk, memoised for the life of a planning pass (G4). Presence, planning and the §5.2
    /// declaration all ask it, and it lists a folder holding thousands of entries.
    /// </summary>
    public ClaudeCodeProjects Look(CancellationToken ct = default) => _projects ??= Walk(ct);

    /// <summary>Drop the memoised walk, so a session written while the app was open is seen.</summary>
    public void Invalidate() => _projects = null;

    private ClaudeCodeProjects Walk(CancellationToken ct)
    {
        if (ClaudeCodeHome.Resolve(_environment) is not { } home)
        {
            return new ClaudeCodeProjects([], NoTranscripts, [], [], Complete: false);
        }

        var root = Path.Combine(home, ClaudeCodeHome.Projects);

        // Not there is a complete answer: Claude Code has written no transcript for this user.
        if (!LongPath.DirectoryExists(root))
        {
            return new ClaudeCodeProjects([], NoTranscripts, [], [], Complete: true);
        }

        if (LongPath.IsReparsePoint(root))
        {
            return new ClaudeCodeProjects([], NoTranscripts, [], [root], Complete: false);
        }

        var scan = ChildDirectories.Under(root);

        if (scan.Unreadable)
        {
            return new ClaudeCodeProjects([], NoTranscripts, [root], [], Complete: false);
        }

        var folders = new List<ClaudeCodeProjectFolder>(scan.Directories.Count);
        var transcripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();

        foreach (var directory in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(directory.FullName);
            var sidecars = new List<ClaudeCodeSidecar>();
            var others = new List<FileSystemInfo>();

            try
            {
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    if (TranscriptSession(entry) is { } session)
                    {
                        transcripts.Add(session);
                    }
                    else if (entry is DirectoryInfo && ClaudeCodeHome.IsSessionId(entry.Name))
                    {
                        sidecars.Add(new ClaudeCodeSidecar(
                            LongPath.Display(entry.FullName),
                            entry.Name,
                            entry.Attributes.HasFlag(FileAttributes.ReparsePoint)));
                    }
                    else
                    {
                        others.Add(entry);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing from this folder, rather than the part listed before the refusal: a
                // transcript not seen is a session some other folder's output would be called an
                // orphan of.
                unreadable.Add(path);
                continue;
            }

            folders.Add(new ClaudeCodeProjectFolder(path, sidecars, others));
        }

        var links = scan.Links.Select(link => LongPath.Display(link.FullName)).ToList();

        return new ClaudeCodeProjects(
            folders,
            transcripts,
            unreadable,
            links,
            Complete: unreadable.Count == 0 && links.Count == 0);
    }

    /// <summary>The session a transcript is named for, or null where the entry is not a transcript.</summary>
    private static string? TranscriptSession(FileSystemInfo entry) =>
        entry is FileInfo
        && entry.Name.EndsWith(TranscriptExtension, StringComparison.OrdinalIgnoreCase)
        && entry.Name[..^TranscriptExtension.Length] is var session
        && ClaudeCodeHome.IsSessionId(session)
            ? session
            : null;
}
