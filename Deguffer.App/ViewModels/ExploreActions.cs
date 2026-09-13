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
    /// <summary>
    /// What Explore says about a path while it is still working out what it must protect.
    ///
    /// <para>A refusal rather than <see cref="ExploreVerdict.Unclassified"/>, because half of §5.2
    /// is not known until the providers have been asked: a moved Go workspace, a conda prefix, a
    /// staging folder an installer is using. Answering "unclassified" in the meantime would allow
    /// exactly the paths this policy exists to refuse, for however long the probes take — and the
    /// user cannot tell a fast answer from a complete one.</para>
    /// </summary>
    private static readonly ExploreVerdict Working = ExploreVerdict.Refuse(
        "Deguffer is still working out what it has to protect on this machine. This will say what it "
        + "is in a moment.");

    private readonly Func<CancellationToken, Task<ExploreActionPolicy>> _build;
    private readonly Func<IExploreConfirmationPrompt> _prompt;

    private Task<ExploreActionPolicy>? _policy;

    /// <param name="build">
    /// How to assemble the policy for this machine. A factory rather than a built policy, because
    /// <see cref="Reconsider"/> has to be able to ask for a fresh one: a declaration that a path is
    /// in use is true of this minute and not of the next.
    /// </param>
    public ExploreActions(
        Func<CancellationToken, Task<ExploreActionPolicy>> build,
        Func<IExploreConfirmationPrompt> prompt)
    {
        _build = build;
        _prompt = prompt;
    }

    /// <summary>Raised once the policy is built, so a page showing <see cref="Working"/> can ask again.</summary>
    public event EventHandler? Ready;

    /// <summary>
    /// The policy for this machine, built the first time something asks.
    ///
    /// <para>Deferred because building it constructs every provider and asks the ones that probe,
    /// and Explore is a page a user may open and never delete anything from. Deferred rather than
    /// skipped because §5.2 is read out of those providers: a policy assembled without them would
    /// refuse the operating system's directories and let a tool's credentials through.</para>
    ///
    /// <para>Each build looks at the machine afresh, and two caches outlive a build unless it says
    /// otherwise. The providers are constructed again, so what each of them resolved goes with the old
    /// ones; they are given their own <see cref="LiveTreeInspector"/>, so what is running is read now
    /// rather than at the last Storage pass; and the executable lookups are cleared, so a toolchain
    /// installed since the page opened is found. That cache holds nothing but where commands are on
    /// <c>PATH</c>, so clearing it while a Storage pass runs costs that pass a repeated lookup with
    /// the same answer.</para>
    /// </summary>
    public static ExploreActions ForThisMachine(Func<IExploreConfirmationPrompt> prompt) =>
        new(
            ct =>
            {
                UserEnvironment.Current.Invalidate();

                return ExploreActionPolicy.ForAsync(
                    SystemDirectories.Current,
                    UserEnvironment.Current,
                    CleanupPlanner.CreateDefault(liveTrees: new LiveTreeInspector()).Providers,
                    ct);
            },
            prompt);

    /// <summary>
    /// Start building the policy, or keep the one already built. Called when the page opens and
    /// whenever a scan starts, so the first selection has an answer waiting rather than a refusal
    /// that has to be taken back.
    /// </summary>
    public void Prepare() => Build();

    /// <summary>
    /// Throw the built policy away, so the next question rebuilds it.
    ///
    /// <para>What is running changes, and a policy holds the answer it was given: a staging folder
    /// an installer had open when the page opened stays refused after the install finishes, and an
    /// entry a program started using since then is allowed. Neither is acceptable indefinitely, and
    /// a scan is the natural moment to ask again — it is the point at which everything else on the
    /// page is being re-measured, and it takes long enough that the probes cost nothing beside
    /// it.</para>
    /// </summary>
    public void Reconsider()
    {
        _policy = null;
        Build();
    }

    /// <summary>
    /// Whether Explore will remove this, and what to say either way (§7.1). <see cref="Working"/>
    /// until the policy is built, which the shell is told about through <see cref="Ready"/>.
    /// </summary>
    public ExploreVerdict Verdict(string path) =>
        Build() is { IsCompletedSuccessfully: true } built ? built.Result.MayRemove(path) : Working;

    /// <summary>
    /// Ask, then remove. Null when the user declined, which is a decision rather than a failure.
    ///
    /// <para>Nothing is asked about an item the policy refuses. A dialog covering something that is
    /// then refused teaches the user that saying yes is how you find out what happens, and §7.1
    /// wants the reason stated instead — so a wholly refused selection goes straight to a report
    /// carrying the reasons.</para>
    ///
    /// <para>It awaits the policy rather than reading whatever is ready, because this is the path
    /// that deletes. A removal decided against a half-built policy is the one thing the deferral
    /// above must never buy.</para>
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

        var policy = await Build().ConfigureAwait(true);
        var (allowed, _) = ExploreRemover.Partition(items, policy);

        if (allowed.Count > 0 &&
            !await _prompt().AskAsync(ExploreRemovalPrompt.For(mode, allowed), ct).ConfigureAwait(true))
        {
            return null;
        }

        // Everything goes back in, refusals included: the remover partitions again and reports what
        // it would not take, so the user is told about each one rather than seeing it silently
        // dropped from the count.
        return await ExploreRemover.RemoveAsync(items, mode, policy, ct: ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The build in flight, started if there is not one.
    ///
    /// <para>No cancellation is passed. The policy belongs to the page rather than to the scan that
    /// happened to start it, and a build cancelled with the scan would leave every later question
    /// answered by a faulted task.</para>
    ///
    /// <para>A build that failed is dropped rather than kept, so the next question starts a new one.
    /// A cached failure would answer every removal for the rest of the session with the same stale
    /// exception, and the condition behind it — a tool the probe could not reach — is usually gone by
    /// the next scan.</para>
    /// </summary>
    private Task<ExploreActionPolicy> Build()
    {
        if (_policy is { IsFaulted: true } or { IsCanceled: true })
        {
            _policy = null;
        }

        return _policy ??= Announce();
    }

    private async Task<ExploreActionPolicy> Announce()
    {
        try
        {
            return await _build(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            // After the await and before the caller resumes, so a shell that asked while this was
            // running is told to ask again. Raised on a failure too: the exception surfaces at
            // whoever awaited the removal, and a page left saying "still working it out" for the
            // rest of the session would be the quieter and worse outcome.
            Ready?.Invoke(this, EventArgs.Empty);
        }
    }
}
