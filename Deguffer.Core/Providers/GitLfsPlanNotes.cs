using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// What a Git LFS prune plan tells the user, and what it names as surviving the prune.
/// </summary>
internal static class GitLfsPlanNotes
{
    public const string SharedReason =
        "This Git LFS store is reached through a link, so other repositories may share it, and it is left alone.";

    public const string NotPrunedReason = "Git LFS's prune is not run for this repository, so its store is left alone.";

    /// <summary>What a git directory holds besides the store, which git needs to open the repository at all.</summary>
    private static readonly (string Name, string Reason)[] GitDirectoryEntries =
    [
        ("HEAD", "Which commit the repository has checked out."),
        ("config", "The repository's own git configuration, its remotes included."),
        ("objects", "The repository's history."),
        ("refs", "The repository's branches and tags."),
        ("index", "What is staged for the next commit."),
        ("packed-refs", "The repository's branches and tags."),
    ];

    public static PlanNote NoStores { get; } = Information(
        "No repository in your source folders keeps files for Git LFS.");

    /// <summary>
    /// How the clean runs, said because it is not what the preview did: the preview never asks the
    /// remote, and the clean asks it about every object, and stops where it cannot. And what every run
    /// of Git LFS does besides, the preview's included: it removes its own stale downloads
    /// (<c>fs/cleanup.go</c>).
    /// </summary>
    public static PlanNote HowItRuns { get; } = Information(
        "Before Git LFS removes anything, it asks each repository's server whether it still has every "
        + "file it would remove. If one is missing there, or the server does not answer or needs you to "
        + "sign in, it removes nothing from that repository. Every time Git LFS runs, for this preview as well "
        + "as for the clean, it also removes its own unfinished downloads that are more than an hour old.");

    public static PlanNote Linked(GitLfsStore store) => Information(
        $"Left the Git LFS files in {store.Storage} alone: the folder is reached through a link, so other "
        + "repositories may share it, and Git LFS's own manual says not to prune a shared store.");

    public static PlanNote NotInstalled(int stores, string said) => new(
        PlanNoteSeverity.Warning,
        (stores == 1
            ? "A repository in your source folders keeps files for Git LFS, "
            : $"{stores} repositories in your source folders keep files for Git LFS, ")
        + $"but Git LFS did not answer, so it may not be installed, and nothing is offered. It said: {said}");

    public static PlanNote DryRunFailed(GitLfsCheckout checkout, string said) => new(
        PlanNoteSeverity.Warning,
        $"Git LFS could not say which old files it would remove from {Describe(checkout)}, so none is offered. It said: {said}");

    public static PlanNote Unread(GitLfsCheckout checkout) => new(
        PlanNoteSeverity.Warning,
        $"Git LFS did not list every old file it would remove from {Describe(checkout)}, so none is offered. "
        + "Measuring the store instead would count the files your checkout uses.");

    /// <summary>The repository as the user knows it: the folder it is checked out in, and the submodule it is.</summary>
    public static string Describe(GitLfsCheckout checkout)
    {
        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(checkout.WorkTree));

        return checkout.Store.Submodule is { } submodule ? $"{folder} (submodule {submodule})" : folder;
    }

    /// <summary>§5.6 for one store: everything around the files prune removes.</summary>
    public static IEnumerable<(string Path, string Reason)> Survivors(GitLfsCheckout checkout) =>
    [
        (checkout.WorkTree, "The repository's checkout. Git LFS's prune removes files from its store and nothing else."),
        (checkout.Store.GitDirectory, "The repository's git directory. Only old files in its Git LFS store are removed."),
        .. GitDirectoryEntries.Select(entry => (Path.Combine(checkout.Store.GitDirectory, entry.Name), entry.Reason)),
        (checkout.Store.Storage, "Git LFS's own folder for this repository."),
        (checkout.Store.Objects, "Git LFS's store, which keeps every file your checkout and recent commits use."),
    ];

    private static PlanNote Information(string message) => new(PlanNoteSeverity.Information, message);
}
