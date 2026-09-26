namespace Deguffer.Core.Providers;

/// <summary>
/// Which projects the running Claude Code sessions may be working in, so that no conversation of one of
/// them is offered.
///
/// <para><b>Why a project and not only a session.</b> A running process can switch to any conversation
/// of its project, by resuming one, at any moment between the preview and the clean. Its entry in the
/// list of running sessions names the session it is in when the entry was written, which is not the one
/// it may be in a moment later.</para>
///
/// <para><b>Two folders per running session, because neither is enough alone.</b> The entry records the
/// folder the process is working in now, and Claude Code rewrites it when the process moves: into a
/// subfolder, or into a git worktree beside the project. A conversation records the folder its session
/// started in. So a session occupies the folder its entry names, every folder that one is inside, and
/// the folder its own conversation says it started in. A session whose entry names no folder, or whose
/// conversation cannot be read, could be in any project.</para>
/// </summary>
internal sealed class ClaudeCodeOccupancy
{
    private static readonly ClaudeCodeOccupancy Everywhere = new([], everywhere: true);

    private readonly IReadOnlyList<string> _folders;
    private readonly bool _everywhere;

    private ClaudeCodeOccupancy(IReadOnlyList<string> folders, bool everywhere)
    {
        _folders = folders;
        _everywhere = everywhere;
    }

    /// <param name="sessions">What the list of running sessions said.</param>
    /// <param name="transcripts">Every conversation's path, by its session's id.</param>
    public static ClaudeCodeOccupancy Of(
        ClaudeCodeSessionList sessions,
        ILookup<string, string> transcripts,
        CancellationToken ct)
    {
        if (!sessions.Complete)
        {
            return Everywhere;
        }

        var folders = new List<string>();

        foreach (var session in sessions.Live)
        {
            if (session.Project is not { } current)
            {
                return Everywhere;
            }

            folders.Add(current);

            // A session new enough to have written no conversation yet started where it is now.
            foreach (var transcript in transcripts[session.SessionId])
            {
                if (ClaudeCodeTranscriptReader.Read(transcript, ct)?.Project is not { } started)
                {
                    return Everywhere;
                }

                folders.Add(started);
            }
        }

        return new ClaudeCodeOccupancy(folders, everywhere: false);
    }

    /// <summary>
    /// Whether a running session may be working in <paramref name="project"/>: in it, somewhere inside it,
    /// or started in it.
    ///
    /// <para>Compared as Claude Code writes both paths, ignoring case and a trailing separator. Nothing
    /// is resolved: a path from another system, such as WSL, is compared as it stands.</para>
    /// </summary>
    public bool Occupies(string project) =>
        _everywhere || _folders.Any(folder => IsWithin(folder, project));

    private static bool IsWithin(string folder, string project)
    {
        var inner = Path.TrimEndingDirectorySeparator(folder.Trim());
        var outer = Path.TrimEndingDirectorySeparator(project.Trim());

        return inner.Equals(outer, StringComparison.OrdinalIgnoreCase)
            || (inner.Length > outer.Length
                && inner.StartsWith(outer, StringComparison.OrdinalIgnoreCase)
                && inner[outer.Length] is '\\' or '/');
    }
}
