using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <param name="Tracked">Candidates git reported as holding tracked files.</param>
/// <param name="Unanswered">
/// Candidates git was asked about and did not answer for. Distinct from <see cref="Tracked"/>
/// because nothing was learned about them either way, and distinct from an empty result because
/// the question was put and failed rather than never being asked at all.
/// </param>
/// <param name="Unasked">
/// Candidates inside a repository that git was never asked about, because git could not be found.
/// Not declined, because the recognition rule is what decides, but counted so the plan can say the
/// check did not run rather than read the same as one that ran and found nothing tracked.
/// </param>
public sealed record TrackedFileFindings(
    IReadOnlySet<string> Tracked,
    IReadOnlySet<string> Unanswered,
    IReadOnlySet<string> Unasked);

/// <summary>
/// Asks git whether any candidate directory contains a tracked file.
///
/// Intermediate output is <c>.gitignore</c>d by every standard .NET template, so a <em>tracked</em>
/// file inside one is evidence that it is not intermediate output at all — whatever the manifest
/// beside it claims. This is a second, independent opinion on the same question the recognition
/// rule answers, and it is the one that would catch a repository which deliberately commits
/// generated files.
///
/// Invocations are per repository, split only where the command line demands it, and never per
/// directory. Candidates are passed as pathspecs, so a source root holding fifty projects across
/// three repositories costs three processes — a per-directory check would cost more than the walk
/// it is protecting.
/// </summary>
public sealed class TrackedFileCheck(IUserEnvironment environment, IProcessRunner runner)
{
    /// <summary>
    /// How many characters of pathspecs one invocation may carry.
    ///
    /// CreateProcess caps a command line at 32,767 characters, and overrunning it fails the launch
    /// outright rather than truncating. The ~2,700 held back covers the fixed prefix — the flags,
    /// <c>ls-files</c>, the <c>--</c> separator — and the quoted repository root, which is the only
    /// other part of the command line that varies in length.
    /// </summary>
    private const int PathspecBudget = 30_000;

    /// <summary>
    /// What git could be told about <paramref name="candidates"/>, compared case-insensitively by
    /// the caller.
    ///
    /// Where git is absent, nothing is found tracked and nothing is declined: this check is
    /// corroboration, and the recognition rule is what decides. A machine without git installed
    /// gets the recognition rule alone, which is the same protection every other provider relies on.
    /// The candidates it would have asked about come back as
    /// <see cref="TrackedFileFindings.Unasked"/>, so that is said rather than silent. Git being
    /// present, being asked, and not answering is a different thing entirely, and comes back as
    /// <see cref="TrackedFileFindings.Unanswered"/> for the caller to decline.
    /// </summary>
    public async Task<TrackedFileFindings> FindTrackedAsync(
        IReadOnlyList<string> candidates,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unanswered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unasked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var git = candidates.Count == 0 ? null : environment.FindExecutable("git");
        var roots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var repository in candidates.GroupBy(
                     candidate => FindRepositoryRoot(candidate, roots),
                     StringComparer.OrdinalIgnoreCase))
        {
            // Before the null-key test, not after it: grouping is lazy, so FindRepositoryRoot's walk
            // up the ancestors runs during this enumeration. A source folder that is not a
            // repository at all would otherwise stat its way through every candidate with no check
            // at all, the batch loop below never being reached.
            ct.ThrowIfCancellationRequested();

            // Candidates outside any repository have nothing to ask about.
            if (repository.Key is not { } root)
            {
                continue;
            }

            // Grouped even without git, so that a candidate outside any repository, which git would
            // not have been asked about either, is not reported as a check that failed to run.
            if (git is null)
            {
                unasked.UnionWith(repository);
                continue;
            }

            var relative = repository.ToDictionary(
                candidate => ToPathspec(root, candidate),
                candidate => candidate,
                StringComparer.OrdinalIgnoreCase);

            foreach (var batch in Batch(relative))
            {
                ct.ThrowIfCancellationRequested();

                // --literal-pathspecs, because these are paths rather than patterns. Without it a
                // project directory containing a glob metacharacter — `App[Legacy]`, `Debug*` — is
                // parsed as a pattern, matches nothing, and the check reports "no tracked files" for
                // a directory it never actually examined. This safeguard failing open is worse than
                // it failing loudly, so the ambiguity is removed rather than assumed away.
                var outcome = await runner.RunAsync(
                    git,
                    $"--literal-pathspecs -C \"{root}\" ls-files -z --{string.Concat(batch.Keys.Select(p => $" \"{p}\""))}",
                    ct).ConfigureAwait(false);

                if (!outcome.Succeeded)
                {
                    // A batch git would not answer for — a broken index, a partial clone, a launch
                    // that failed. The same reasoning as --literal-pathspecs applies: reporting "no
                    // tracked files" for directories that were never examined is precisely the
                    // failure this check exists to prevent, so they are declined rather than passed.
                    unanswered.UnionWith(batch.Values);
                    continue;
                }

                MarkTracked(outcome.StandardOutput, batch, tracked);
            }
        }

        return new TrackedFileFindings(tracked, unanswered, unasked);
    }

    /// <summary>
    /// Split the pathspecs into invocations that each fit <see cref="PathspecBudget"/>.
    ///
    /// A repository contributing a few hundred deep paths overruns a single command line, and the
    /// launch failure that follows is indistinguishable at the call site from a clean "nothing is
    /// tracked". Grouping by repository is preserved: batching exists to keep the command line
    /// legal, not to reintroduce the process-per-directory cost that grouping avoids.
    /// </summary>
    private static IEnumerable<Dictionary<string, string>> Batch(IReadOnlyDictionary<string, string> byPathspec)
    {
        var batch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var length = 0;

        foreach (var (pathspec, candidate) in byPathspec)
        {
            // The separating space, and the two quotes the argument string wraps the path in.
            var cost = pathspec.Length + 3;

            // A single pathspec over the budget on its own still goes out alone rather than being
            // dropped: git is then the one that refuses it, and a refusal declines the candidate.
            if (batch.Count > 0 && length + cost > PathspecBudget)
            {
                yield return batch;
                batch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                length = 0;
            }

            batch.Add(pathspec, candidate);
            length += cost;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
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
    ///
    /// Every directory the walk passes is remembered in <paramref name="known"/> with the answer it
    /// led to, because sibling projects share almost all of their ancestors, and without it each
    /// candidate would probe the same directories again. The answer for a directory is the nearest
    /// repository at or above it, so it holds for every candidate beneath that directory.
    /// </summary>
    private static string? FindRepositoryRoot(string candidate, Dictionary<string, string?> known)
    {
        var visited = new List<string>();
        string? root = null;

        for (var directory = Path.GetDirectoryName(candidate.TrimEnd(Path.DirectorySeparatorChar));
             !string.IsNullOrEmpty(directory);
             directory = Path.GetDirectoryName(directory))
        {
            if (known.TryGetValue(directory, out root))
            {
                break;
            }

            visited.Add(directory);
            var git = Path.Combine(directory, ".git");

            if (LongPath.DirectoryExists(git) || LongPath.FileExists(git))
            {
                root = directory;
                break;
            }
        }

        foreach (var directory in visited)
        {
            known[directory] = root;
        }

        return root;
    }
}
