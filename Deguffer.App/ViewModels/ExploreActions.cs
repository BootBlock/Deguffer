using Deguffer.App.Shell;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.App.ViewModels;

/// <summary>
/// Asks about an Explore removal and carries it out.
///
/// <para>Separate from <see cref="ExploreViewModel"/> because the two have different subjects. That
/// one is about which node is being looked at and what the screen says about it; this one is about
/// what happens to a thing the user picked. Keeping them apart is G1 applied to a page that would
/// otherwise be scanning, navigating, formatting <em>and</em> deleting.</para>
///
/// <para>It decides nothing. What may be removed is <see cref="ExploreActionPolicy"/>'s, what the
/// user is told is <see cref="ExploreRemovalPrompt"/>'s, and what happened is
/// <see cref="ExploreRemovalReport.Summary"/>'s — all in Core, all provable without a WinUI
/// host.</para>
/// </summary>
public sealed class ExploreActions
{
    private readonly Lazy<ExploreActionPolicy> _policy;
    private readonly Func<IExploreConfirmationPrompt> _prompt;

    public ExploreActions(Lazy<ExploreActionPolicy> policy, Func<IExploreConfirmationPrompt> prompt)
    {
        _policy = policy;
        _prompt = prompt;
    }

    /// <summary>
    /// The policy for this machine, built the first time something asks.
    ///
    /// <para>Deferred because constructing it constructs every provider, and Explore is a page a
    /// user may open and never delete anything from. Deferred rather than skipped because §5.2 is
    /// read out of those providers: a policy assembled without them would refuse the operating
    /// system's directories and let a tool's credentials through.</para>
    /// </summary>
    public static ExploreActions ForThisMachine(Func<IExploreConfirmationPrompt> prompt) =>
        new(
            new Lazy<ExploreActionPolicy>(() => ExploreActionPolicy.For(
                SystemDirectories.Current,
                UserEnvironment.Current,
                VolumeInventory.Current,
                CleanupPlanner.CreateDefault().Providers)),
            prompt);

    /// <summary>Whether Explore will remove this, and what to say either way (§7.1).</summary>
    public ExploreVerdict Verdict(string path) => _policy.Value.MayRemove(path);

    /// <summary>
    /// Ask, then remove. Null when the user declined, which is a decision rather than a failure.
    ///
    /// <para>Nothing is asked about an item the policy refuses. A dialog covering something that is
    /// then refused teaches the user that saying yes is how you find out what happens, and §7.1
    /// wants the reason stated instead — so a wholly refused selection goes straight to a report
    /// carrying the reasons.</para>
    /// </summary>
    public async Task<ExploreRemovalReport?> RemoveAsync(
        IReadOnlyList<ExploreItem> items,
        ExploreRemovalMode mode,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return null;
        }

        var (allowed, _) = ExploreRemover.Partition(items, _policy.Value);

        if (allowed.Count > 0 &&
            !await _prompt().AskAsync(ExploreRemovalPrompt.For(mode, allowed), ct).ConfigureAwait(true))
        {
            return null;
        }

        // Everything goes back in, refusals included: the remover partitions again and reports what
        // it would not take, so the user is told about each one rather than seeing it silently
        // dropped from the count.
        return await ExploreRemover.RemoveAsync(items, mode, _policy.Value, ct: ct).ConfigureAwait(true);
    }
}
