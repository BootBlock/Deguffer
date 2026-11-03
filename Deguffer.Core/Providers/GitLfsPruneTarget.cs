using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether <c>git lfs prune</c>, run now, would still act on the repository's own store while
/// nothing is working in the repository.
///
/// <para><b>Prune takes no lock, and decides what to keep from what git says when it runs.</b> An
/// object <c>git add</c> has stored but not yet recorded in the index is one prune does not know is
/// wanted, and can remove (<c>commands/command_prune.go</c>, git-lfs 3.8.1). A program working in the
/// repository, an editor's own git included, is how that happens, so the step does not run while
/// one is, as a build directory is not removed under one. The preview asked the same question.</para>
///
/// <para><b>And a link made since would share the store.</b> The command pins <c>lfs.storage</c> to
/// the store's folder (<see cref="GitLfsCheckout"/>), and that folder reached through a junction is
/// another repository's store too.</para>
/// </summary>
internal sealed class GitLfsPruneTarget(ILiveTreeInspector inspector, GitLfsCheckout checkout) : ICommandTargetCheck
{
    public string? WhyNot(RunCommandStep step, CancellationToken ct)
    {
        switch (GitLfsStores.StandingOf(checkout.Store))
        {
            case GitLfsStoreStanding.Absent:
                return $"{checkout.Store.Objects} is no longer there";

            case GitLfsStoreStanding.Unreachable:
                return $"Windows would not describe {checkout.Store.Objects}, so Deguffer cannot tell whether it is still "
                    + "this repository's own store";

            case GitLfsStoreStanding.Linked:
                return $"{checkout.Store.Objects} is now reached through a link, so it may be a store other repositories "
                    + "share, which Git LFS's manual says not to prune";
        }

        // The table the preview or an earlier check read is dropped where it is past the limit, as a
        // build directory's check drops it.
        inspector.DiscardOlderThan(IUseCheck.ReadingLimit);

        var asked = GitLfsObjectProvider.InUseQuery(checkout.Store.GitDirectory, checkout.WorkTree);
        var findings = inspector.FindLive([asked], ct);

        if (findings.Live.FirstOrDefault(tree => tree.Directory.Equals(asked.Directory, StringComparison.OrdinalIgnoreCase)) is { } live)
        {
            return $"{string.Join("; ", live.Holders)} is working in {checkout.WorkTree}, and Git LFS's prune could "
                + "remove an object git is still adding. Close it and clean again";
        }

        return findings.IsAnswered(asked.Directory)
            ? null
            : $"{LiveTreeVeto.CannotTell} repository";
    }
}
