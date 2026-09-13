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

    /// <summary>
    /// What Explore says about every path once a build has failed, until one succeeds.
    ///
    /// <para>A refusal for the reason <see cref="Working"/> is one: without the probed half there is
    /// no knowing what else would have been refused. It says what happened rather than "in a moment",
    /// because nothing is on its way until the next scan asks again.</para>
    /// </summary>
    private static readonly ExploreVerdict Failed = ExploreVerdict.Refuse(
        "Deguffer could not work out what it has to protect on this machine, so Explore removes "
        + "nothing for now. Scanning again tries once more.");

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

    /// <summary>
    /// Raised on the thread that started a build once that build has finished, whether it succeeded
    /// or failed, so a page that stated <see cref="Working"/> can ask again.
    /// </summary>
    public event EventHandler? Ready;

    /// <summary>
    /// Actions over a policy for this machine, which is built in the background when the page is
    /// constructed and again at each scan.
    ///
    /// <para>In the background because building it constructs every provider and runs their probes,
    /// which would otherwise hold up the page. Never skipped, because §5.2 is read out of those
    /// providers: a policy assembled without them would refuse the operating system's directories
    /// and let a tool's credentials through.</para>
    ///
    /// <para>Each build looks at the machine afresh. The providers are constructed again, so what
    /// each of them resolved goes with the old ones, and they are given their own
    /// <see cref="LiveTreeInspector"/>, so what is running is read at the build rather than at the
    /// last Storage pass. Where commands are on <c>PATH</c> is the one answer shared with the Storage
    /// page: <see cref="UserEnvironment.Current"/> remembers it until a planning pass clears it, and
    /// clearing it from here would change what a pass already under way sees.</para>
    /// </summary>
    public static ExploreActions ForThisMachine(Func<IExploreConfirmationPrompt> prompt) =>
        new(
            ct => ExploreActionPolicy.ForAsync(
                SystemDirectories.Current,
                UserEnvironment.Current,
                CleanupPlanner.CreateDefault(liveTrees: new LiveTreeInspector()).Providers,
                ct),
            prompt);

    /// <summary>
    /// Start building the policy, unless one is built or on its way. Called once, when the page is
    /// constructed, so the first selection has an answer waiting rather than a refusal that has to be
    /// taken back. The page is kept while the app runs, so a return visit keeps the policy it had, and
    /// a scan builds a new one.
    /// </summary>
    public void Prepare()
    {
        if (_policy is null or { IsFaulted: true } or { IsCanceled: true })
        {
            Start();
        }
    }

    /// <summary>
    /// Build the policy again, replacing the one there is.
    ///
    /// <para>What is running changes, and a policy holds the answer it was given: a staging folder
    /// an installer had open when the page opened stays refused after the install finishes, and an
    /// entry a program started using since then is allowed. Neither is acceptable indefinitely, and
    /// a scan is the natural moment to ask again — it is the point at which everything else on the
    /// page is being re-measured, and it takes long enough that the probes cost nothing beside
    /// it.</para>
    /// </summary>
    public void Reconsider() => Start();

    /// <summary>
    /// Whether Explore will remove this, and what to say either way (§7.1): <see cref="Working"/>
    /// while the policy is being built, and <see cref="Failed"/> once a build has failed. The shell
    /// is told through <see cref="Ready"/> when that changes.
    ///
    /// <para>It starts nothing. The page opening, a scan and a removal each start a build, and a
    /// question that restarted a failed one would run every provider's probes again each time the
    /// selection changed.</para>
    /// </summary>
    public ExploreVerdict Verdict(string path) => _policy switch
    {
        { IsCompletedSuccessfully: true } built => built.Result.MayRemove(path),
        { IsFaulted: true } or { IsCanceled: true } => Failed,
        _ => Working,
    };

    /// <summary>
    /// Ask, then remove. Null when the user declined, which is a decision rather than a failure.
    ///
    /// <para>Nothing is asked about an item the policy refuses. A dialog covering something that is
    /// then refused teaches the user that saying yes is how you find out what happens, and §7.1
    /// wants the reason stated instead — so a wholly refused selection goes straight to a report
    /// carrying the reasons.</para>
    ///
    /// <para>It waits for the policy rather than reading whatever is ready, because this is the path
    /// that deletes. A removal decided against a half-built policy is the one thing the deferral
    /// above must never buy, and one whose build failed refuses everything it was given.</para>
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

        var building = _policy is { IsFaulted: false, IsCanceled: false } current ? current : Start();

        // Waited for rather than awaited, so a build that fails reaches the user as a refusal of
        // what they picked rather than as an exception out of the command that deletes.
        await Task.WhenAny(building).ConfigureAwait(true);

        if (!building.IsCompletedSuccessfully)
        {
            return new ExploreRemovalReport(
                mode,
                [.. items.Select(item => new ExploreItemOutcome(item.Path, Removed: false, Bytes: 0, Failed.Reason))],
                new VerificationResult());
        }

        var policy = building.Result;
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
    /// Build a new policy and keep it, in place of any other.
    ///
    /// <para>No cancellation is passed. The policy belongs to the page rather than to the scan that
    /// happened to start it, and a build cancelled with the scan would answer every later question
    /// with <see cref="Failed"/>.</para>
    /// </summary>
    private Task<ExploreActionPolicy> Start()
    {
        var building = BuildAsync();

        _policy = building;
        _ = AnnounceAsync(building);

        return building;
    }

    /// <summary>
    /// The factory's task, with a factory that throws before it returns one turned into a failed
    /// build. What it does before its first await, constructing every provider, would otherwise
    /// throw out of whichever caller started it, the page's own constructor among them.
    /// </summary>
    private async Task<ExploreActionPolicy> BuildAsync() =>
        await _build(CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Raise <see cref="Ready"/> once <paramref name="building"/> has finished, on the thread that
    /// started it.
    ///
    /// <para>After the build's task has completed, never from inside the build. A handler raised
    /// before completion asks <see cref="Verdict"/> about a task that has not finished, is told
    /// <see cref="Working"/> again, and is never told otherwise.</para>
    ///
    /// <para>Only for the build still kept. One replaced while it ran has nothing to tell a page that
    /// is now waiting on its successor.</para>
    /// </summary>
    private async Task AnnounceAsync(Task<ExploreActionPolicy> building)
    {
        await Task.WhenAny(building).ConfigureAwait(true);

        // Recorded here because the page cannot show it: the refusal says a build failed, and the
        // log says which provider's probe failed and how. Reading the exception observes it, so it is
        // not reported a second time when the task is collected.
        if (building.Exception is { } failure)
        {
            App.Faults.Record("Building Explore's removal policy", failure);
        }

        if (ReferenceEquals(building, _policy))
        {
            Ready?.Invoke(this, EventArgs.Empty);
        }
    }
}
