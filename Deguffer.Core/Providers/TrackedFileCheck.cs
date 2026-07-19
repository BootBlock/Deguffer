using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Asks git whether any candidate directory contains a tracked file.
///
/// Intermediate output is <c>.gitignore</c>d by every standard .NET template, so a <em>tracked</em>
/// file inside one is evidence that it is not intermediate output at all — whatever the manifest
/// beside it claims. This is a second, independent opinion on the same question the recognition
/// rule answers, and it is the one that would catch a repository which deliberately commits
/// generated files.
///
/// One invocation per repository, never per directory. Candidates are passed as pathspecs in a
/// single call, so a source root holding fifty projects across three repositories costs three
/// processes — a per-directory check would cost more than the walk it is protecting.
/// </summary>
public sealed class TrackedFileCheck(IUserEnvironment environment, IProcessRunner runner)
{
    /// <summary>
    /// Those of <paramref name="candidates"/> that git reports as holding tracked files, compared
    /// case-insensitively by the caller.
    ///
    /// An empty result where git is absent is deliberate rather than a silent failure: this check
    /// is corroboration, and the recognition rule is what decides. A machine without git installed
    /// gets the recognition rule alone, which is the same protection every other provider relies on.
    /// </summary>
    public async Task<IReadOnlySet<string>> FindTrackedAsync(
        IReadOnlyList<string> candidates,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (candidates.Count == 0 || environment.FindExecutable("git") is not { } git)
        {
            return tracked;
        }

        foreach (var repository in candidates.GroupBy(FindRepositoryRoot, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            // Candidates outside any repository have nothing to ask about.
            if (repository.Key is not { } root)
            {
                continue;
            }

            var relative = repository.ToDictionary(
                candidate => ToPathspec(root, candidate),
                candidate => candidate,
                StringComparer.OrdinalIgnoreCase);

            // --literal-pathspecs, because these are paths rather than patterns. Without it a
            // project directory containing a glob metacharacter — `App[Legacy]`, `Debug*` — is
            // parsed as a pattern, matches nothing, and the check reports "no tracked files" for a
            // directory it never actually examined. This safeguard failing open is worse than it
            // failing loudly, so the ambiguity is removed rather than assumed away.
            var outcome = await runner.RunAsync(
                git,
                $"--literal-pathspecs -C \"{root}\" ls-files -z --{string.Concat(relative.Keys.Select(p => $" \"{p}\""))}",
                ct).ConfigureAwait(false);

            if (!outcome.Succeeded)
            {
                // A repository git will not answer for — a broken index, a partial clone. The
                // recognition rule still governs; this adds nothing rather than blocking.
                continue;
            }

            MarkTracked(outcome.StandardOutput, relative, tracked);
        }

        return tracked;
    }

    /// <summary>
    /// Attribute each tracked path git listed back to the candidate that contains it. Output is NUL
    /// separated (<c>-z</c>) because git otherwise quotes and escapes any path with a non-ASCII or
    /// unusual character, and a mangled path would silently fail to match its candidate.
    /// </summary>
    private static void MarkTracked(
        string output,
        IReadOnlyDictionary<string, string> byPathspec,
        HashSet<string> tracked)
    {
        foreach (var line in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var (pathspec, candidate) in byPathspec)
            {
                if (line.StartsWith(pathspec + '/', StringComparison.OrdinalIgnoreCase))
                {
                    tracked.Add(candidate);
                }
            }
        }
    }

    /// <summary>A repository-relative path in the form git speaks: forward slashes, no drive.</summary>
    private static string ToPathspec(string repositoryRoot, string candidate) =>
        Path.GetRelativePath(repositoryRoot, candidate).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// The repository containing <paramref name="candidate"/>, or null if it is not in one.
    ///
    /// Found by walking up for <c>.git</c> rather than by asking git: this runs per candidate, and
    /// the whole point of grouping is to avoid a process per directory. <c>.git</c> is matched as
    /// either a directory or a file, because a worktree and a submodule both record it as a file.
    /// </summary>
    private static string? FindRepositoryRoot(string candidate)
    {
        for (var directory = Path.GetDirectoryName(candidate.TrimEnd(Path.DirectorySeparatorChar));
             !string.IsNullOrEmpty(directory);
             directory = Path.GetDirectoryName(directory))
        {
            var git = Path.Combine(directory, ".git");

            if (LongPath.DirectoryExists(git) || LongPath.FileExists(git))
            {
                return directory;
            }
        }

        return null;
    }
}
