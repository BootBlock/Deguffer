using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One Git LFS store and the work tree it is checked out in, and the arguments that send git to
/// exactly those two.
///
/// <para><b>Every argument names its target, so nothing is left for git to work out.</b> git is
/// started in Deguffer's own folder (<see cref="ProcessRunner"/>), so <c>--git-dir</c> and
/// <c>--work-tree</c> are given rather than <c>-C</c>, which leaves git to find them. It starts without
/// the variables that name another index, object store or set of refs (<see cref="GitEnvironment"/>),
/// which git reads over these arguments. <c>lfs.storage</c> is set on the command line to the store the plan
/// measured, because git-lfs otherwise reads it from configuration when it runs: a value set after the
/// preview would send prune to a store shared with other repositories, which its manual says must
/// never be pruned. Observed against git-lfs 3.8.1: with <c>lfs.storage</c> configured elsewhere,
/// this still prunes the repository's own store.</para>
///
/// <para>Paths are given with forward slashes, which git and git-lfs read as Windows paths, so a
/// path that ends in a separator, such as a drive's root, cannot escape its closing quote.</para>
/// </summary>
/// <param name="Remotes">The names of the repository's remotes, as git reads them from <c>remote.&lt;name&gt;.url</c>.</param>
internal sealed record GitLfsCheckout(GitLfsStore Store, string WorkTree, IReadOnlyList<string> Remotes)
{
    /// <summary>
    /// What Git LFS reports about the repository, its endpoints and the remote prune checks among
    /// them, read with the pins every other command has (<see cref="GitLfsRemote"/>).
    /// </summary>
    public string EnvironmentArguments => Arguments("lfs env");

    /// <summary>The argument that sends git to <paramref name="store"/>'s git directory, for this and for every query about it.</summary>
    public static string GitDirectoryArgument(GitLfsStore store) => Quoted("--git-dir=" + Forward(store.GitDirectory));

    /// <summary><paramref name="command"/>, sent to this git directory, work tree and store.</summary>
    public string Arguments(string command) =>
        $"{GitDirectoryArgument(Store)} {Quoted("--work-tree=" + Forward(WorkTree))} "
        + $"-c {Quoted("lfs.storage=" + Forward(Store.Storage))} {command}";

    /// <summary>One argument, quoted whole, so a path or a remote's name with spaces in it stays one.</summary>
    public static string Quoted(string argument) => $"\"{argument}\"";

    private static string Forward(string path) => LongPath.Display(path).Replace('\\', '/');
}

/// <summary>A store's checkout, or why it is not offered.</summary>
/// <param name="WhyNot">Why <paramref name="Checkout"/> is null, as a sentence for the plan.</param>
/// <param name="Unanswered">
/// Whether git or Windows could not answer, rather than answered with a reason not to prune. A row
/// holding only those was not examined.
/// </param>
internal sealed record GitLfsCheckoutReading(GitLfsCheckout? Checkout, string WhyNot, bool Unanswered);

/// <summary>
/// What git's configuration says about a store: where it is checked out, whether git-lfs keeps its
/// objects somewhere else, and which remotes the repository has. One <c>git config</c> per store.
/// </summary>
internal static class GitLfsSettings
{
    private const string WorkTreeKey = "core.worktree";

    private const string StorageKey = "lfs.storage";

    private const string RemotePrefix = "remote.";

    private const string RemoteSuffix = ".url";

    /// <summary>
    /// Every key at once, each separated from its value by a line feed and from the next by a null
    /// character, so a value with spaces or line ends in it is read whole. git lower-cases a key's
    /// section and name, and keeps a remote's name as written.
    /// </summary>
    private const string Query = $"config -z --get-regexp \"^(core\\.worktree|lfs\\.storage|remote\\..*\\.url)$\"";

    public static async Task<GitLfsCheckoutReading> ReadAsync(
        IProcessRunner runner,
        string git,
        GitLfsStore store,
        CancellationToken ct)
    {
        var outcome = await runner.RunAsync(
            git,
            $"{GitLfsCheckout.GitDirectoryArgument(store)} {Query}",
            GitEnvironment.Query,
            ct).ConfigureAwait(false);

        // git config exits 1 where no key matches, which here is the ordinary answer.
        if (outcome.ExitCode is not (0 or 1))
        {
            return Unanswered($"git could not read its configuration for {store.GitDirectory}. It said: {outcome.Message}");
        }

        string? workTree = null;
        string? storage = null;
        var remotes = new List<string>();

        foreach (var entry in outcome.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // The runner hands output over line by line, each line ended with a carriage return and a
            // line feed (ToolProcess), so the line end after a key is two characters, and the line end
            // after the last null character is an entry of its own.
            var split = entry.IndexOf('\n', StringComparison.Ordinal);
            var (key, value) = split < 0 ? (entry, string.Empty) : (entry[..split].Trim('\r', '\n'), entry[(split + 1)..]);

            // The last value wins, as it does for git.
            switch (key)
            {
                case WorkTreeKey:
                    workTree = value;
                    break;

                case StorageKey:
                    storage = value;
                    break;

                case var remote when remote.Length > RemotePrefix.Length + RemoteSuffix.Length
                    && remote.StartsWith(RemotePrefix, StringComparison.Ordinal)
                    && remote.EndsWith(RemoteSuffix, StringComparison.Ordinal):
                    remotes.Add(remote[RemotePrefix.Length..^RemoteSuffix.Length]);
                    break;
            }
        }

        if (storage is not null && !NamesTheStore(storage, store))
        {
            return Refused(
                $"Git LFS is set to keep the objects for {store.GitDirectory} in '{storage}' (lfs.storage). Git LFS's own "
                + "manual says not to prune a store that several repositories may share, so it is left alone.");
        }

        if (WorkTreeOf(workTree, store) is not { } checkout)
        {
            return Refused(
                $"git does not say where the submodule {store.Submodule} is checked out ({WorkTreeKey} is not set in "
                + $"{store.GitDirectory}), so Git LFS cannot tell which of its objects the checkout needs.");
        }

        return LongPath.ProbeDirectory(checkout) switch
        {
            PathPresence.Present => new GitLfsCheckoutReading(new GitLfsCheckout(store, checkout, remotes), string.Empty, Unanswered: false),
            PathPresence.Refused => Unanswered(UnreadableRoot.WhyItCouldNotBeReached(checkout)),
            _ => Refused(
                $"{store.GitDirectory} is checked out in {checkout}, which is not there, so Git LFS cannot tell which of "
                + "its objects the checkout needs."),
        };
    }

    /// <summary>
    /// Where git takes the work tree to be. A relative <c>core.worktree</c> is relative to the git
    /// directory. Unset, a repository's work tree is the folder holding its <c>.git</c>, and a
    /// submodule's is unknown, because its git directory is not inside it.
    /// </summary>
    private static string? WorkTreeOf(string? configured, GitLfsStore store) =>
        configured is { Length: > 0 }
            ? Resolved(configured, store)
            : store.Submodule is null
                ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(store.GitDirectory))
                : null;

    /// <summary>
    /// Whether a configured <c>lfs.storage</c> is the store's own folder anyway. A relative one is
    /// relative to the git directory (<c>fs/fs.go</c>).
    /// </summary>
    private static bool NamesTheStore(string configured, GitLfsStore store) =>
        Resolved(configured, store) is { } resolved
        && Path.TrimEndingDirectorySeparator(resolved).Equals(
            Path.TrimEndingDirectorySeparator(LongPath.Display(store.Storage)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>A configured path as git reads it: as it stands where it is absolute, and relative to the git directory where not.</summary>
    private static string? Resolved(string configured, GitLfsStore store) =>
        LongPath.Configured(Path.IsPathFullyQualified(configured) ? configured : Path.Combine(store.GitDirectory, configured));

    private static GitLfsCheckoutReading Refused(string why) => new(null, why, Unanswered: false);

    private static GitLfsCheckoutReading Unanswered(string why) => new(null, why, Unanswered: true);
}
