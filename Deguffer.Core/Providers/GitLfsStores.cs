using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A git directory that keeps a Git LFS object store, as <see cref="GitLfsStores"/> found it.
/// </summary>
/// <param name="GitDirectory">The git directory: a repository's <c>.git</c>, or a submodule's directory under it.</param>
/// <param name="Submodule">
/// The submodule's path below the repository's git directory's <c>modules</c> folder, such as
/// <c>libs\art</c>, or null for the repository's own git directory. A submodule's work tree is
/// wherever its configuration says, so only git can name it (<see cref="GitLfsSettings"/>).
/// </param>
internal sealed record GitLfsStore(string GitDirectory, string? Submodule)
{
    /// <summary>Where git-lfs keeps its files while <c>lfs.storage</c> is unset: <c>lfs</c> in the git directory.</summary>
    public string Storage => Path.Combine(GitDirectory, "lfs");

    /// <summary>The store prune removes object files from.</summary>
    public string Objects => Path.Combine(Storage, "objects");
}

/// <summary>What a git directory's Git LFS store is, as <see cref="GitLfsStores.StandingOf"/> reads it.</summary>
internal enum GitLfsStoreStanding
{
    /// <summary>The git directory keeps no store: Git LFS has never stored an object for it.</summary>
    Absent,

    /// <summary>A store of the git directory's own, which prune may act on.</summary>
    Own,

    /// <summary>
    /// The store, or the <c>lfs</c> folder above it, is a junction or a symbolic link, which is how
    /// several repositories share one store. Git LFS's manual says not to prune a shared one.
    /// </summary>
    Linked,

    /// <summary>Windows would not describe the store or the folder above it.</summary>
    Unreachable,
}

/// <summary>What <see cref="GitLfsStores.In"/> found in one repository's git directory.</summary>
/// <param name="Recognised">
/// False where the folder named <c>.git</c> holds no <c>HEAD</c> file, so it is not a git directory
/// and nothing in it was looked at.
/// </param>
/// <param name="Stores">Every git directory in it that keeps a store of its own: the repository's, then its submodules'.</param>
/// <param name="Linked">Every git directory in it whose store is reached through a link, and so is not offered.</param>
/// <param name="Unreachable">Folders Windows would not describe or list, so whether they hold a store is unknown.</param>
internal sealed record GitLfsSurvey(
    bool Recognised,
    IReadOnlyList<GitLfsStore> Stores,
    IReadOnlyList<GitLfsStore> Linked,
    IReadOnlyList<string> Unreachable);

/// <summary>
/// Which git directories in a repository keep a Git LFS object store, read from the folders alone.
///
/// <para><b>Submodules have stores of their own.</b> git keeps a submodule's git directory in the
/// repository's, at <c>.git\modules\&lt;name&gt;</c>, where a name may hold slashes and a nested
/// submodule's is in its parent's <c>modules</c> in turn. Prune acts on one git directory and never on
/// a submodule's (<c>commands/command_prune.go</c> does not recurse), so each is offered on its own.
/// The source search never enters <c>.git</c>, so this is the only route to them, and it reads folder
/// names and nothing else.</para>
///
/// <para>A linked worktree, whose <c>.git</c> is a file, shares its repository's git directory and so
/// its store, which is found in the repository and pruned once there. Prune keeps what every
/// worktree has checked out.</para>
/// </summary>
internal static class GitLfsStores
{
    private const string ModulesFolder = "modules";

    /// <summary>The file every git directory holds, and the one git itself takes as the sign of one.</summary>
    private const string HeadFile = "HEAD";

    /// <param name="gitDirectory">A folder named <c>.git</c> that the source search found.</param>
    public static GitLfsSurvey In(string gitDirectory, CancellationToken ct)
    {
        // Two states, not three: HEAD decides what the folder is, and a refusal is no evidence that it
        // is a git directory.
        if (LongPath.ProbeFile(Path.Combine(gitDirectory, HeadFile)) is not PathPresence.Present)
        {
            return new GitLfsSurvey(Recognised: false, [], [], []);
        }

        var found = new Found();

        Collect(gitDirectory, submodule: null, found, ct);

        return new GitLfsSurvey(Recognised: true, found.Stores, found.Linked, found.Unreachable);
    }

    /// <summary>
    /// What <paramref name="store"/> is now. Asked by the survey, and again by the clean immediately
    /// before prune runs, because a link made in between would send prune to a shared store.
    /// </summary>
    public static GitLfsStoreStanding StandingOf(GitLfsStore store)
    {
        foreach (var folder in (ReadOnlySpan<string>)[store.Storage, store.Objects])
        {
            switch (LongPath.ProbeDirectory(folder, out var isLink))
            {
                case PathPresence.Absent:
                    return GitLfsStoreStanding.Absent;

                case PathPresence.Refused:
                    return GitLfsStoreStanding.Unreachable;

                case PathPresence.Present when isLink is not false:
                    return GitLfsStoreStanding.Linked;
            }
        }

        return GitLfsStoreStanding.Own;
    }

    /// <summary><paramref name="gitDirectory"/>'s own store, where it keeps one, and each submodule's below it.</summary>
    private static void Collect(string gitDirectory, string? submodule, Found found, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var store = new GitLfsStore(gitDirectory, submodule);

        switch (StandingOf(store))
        {
            case GitLfsStoreStanding.Own:
                found.Stores.Add(store);
                break;

            case GitLfsStoreStanding.Linked:
                found.Linked.Add(store);
                break;

            case GitLfsStoreStanding.Unreachable:
                found.Unreachable.Add(store.Objects);
                break;
        }

        var modules = Path.Combine(gitDirectory, ModulesFolder);

        // A link here is not followed, for the reason Submodules gives.
        switch (LongPath.ProbeDirectory(modules, out var isLink))
        {
            case PathPresence.Present when isLink is false:
                Submodules(modules, modules, found, ct);
                break;

            case PathPresence.Refused:
                found.Unreachable.Add(modules);
                break;
        }
    }

    /// <summary>
    /// Every submodule git directory at or below <paramref name="folder"/>. A folder holding
    /// <c>HEAD</c> is one, and is searched further only through its own <c>modules</c>. Any other
    /// folder is part of a name with slashes in it. A link is never followed, so the search can neither
    /// leave the repository nor go round in a circle.
    /// </summary>
    /// <param name="modules">The <c>modules</c> folder the search started in, which names are read from.</param>
    private static void Submodules(string modules, string folder, Found found, CancellationToken ct)
    {
        var children = ChildDirectories.Under(folder);

        if (children.Unreadable)
        {
            found.Unreachable.Add(folder);
            return;
        }

        foreach (var child in children.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(child.FullName);

            switch (LongPath.ProbeFile(Path.Combine(path, HeadFile)))
            {
                case PathPresence.Present:
                    Collect(path, LongPath.Relative(modules, path), found, ct);
                    break;

                case PathPresence.Absent:
                    Submodules(modules, path, found, ct);
                    break;

                // Whether this is a git directory or part of a name is unknown, so neither reading is
                // acted on.
                case PathPresence.Refused:
                    found.Unreachable.Add(path);
                    break;
            }
        }
    }

    /// <summary>The survey's lists while they are being filled.</summary>
    private sealed class Found
    {
        public List<GitLfsStore> Stores { get; } = [];

        public List<GitLfsStore> Linked { get; } = [];

        public List<string> Unreachable { get; } = [];
    }
}
