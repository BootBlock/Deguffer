using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Duplicates;

/// <summary>What asking for a duplicate removal came to.</summary>
/// <param name="Confirmation">What the user was asked, or would have been: the marks that stood as it was built.</param>
/// <param name="Report">What the removal did, or null where nothing was asked, the user declined, or the marks stopped applying.</param>
/// <param name="Withdrawn">Why the marks stopped applying before anything was removed, or null.</param>
public sealed record DuplicateRemovalAnswer(RemovalConfirmation Confirmation, DuplicateRemovalReport? Report, string? Withdrawn = null)
{
    /// <summary>What happened, in a sentence for the page.</summary>
    public string Summary => Withdrawn
        ?? (Report is { } report
            ? report.Summary + Stayed
            : Confirmation.Copies.Count > 0
                ? "Nothing was removed."
                : Confirmation.Staying.Count > 0
                    ? "No marked copy can go to the Recycle Bin as things are now, so nothing was asked or removed. Each copy says why."
                    : "No marked copy can go as things are now, so nothing was asked or removed. Each copy says why.");

    /// <summary>What became of the copies the Recycle Bin could not take, which the removal was not handed.</summary>
    private string Stayed => Confirmation.Staying.Count switch
    {
        0 => string.Empty,
        1 => " 1 marked copy stayed, because the Recycle Bin cannot take it.",
        var count => $" {count:N0} marked copies stayed, because the Recycle Bin cannot take them.",
    };
}

/// <summary>
/// Runs a rule over a search's marks, and asks about and carries out the removal of the marks that
/// stand (§7.4), each off the page's thread.
///
/// <para>Separate from the page's view-models for the reason <see cref="ExploreActions"/> is: what it
/// holds are rules about what gets deleted. Nothing is asked about nothing: where no mark stands
/// once the marks are judged again, or the Recycle Bin can take none of the copies bound for it, the
/// user is not shown a dialog to say yes to. The removal judges
/// the marks again against protections built afresh after the user said yes, as
/// <see cref="DuplicateRemover.RemoveAsync"/> asks, because a confirmation can stay open for as long
/// as the user reads it. It is recorded as running from the moment it is confirmed until its §5.6
/// check has reported, so nothing ends the process under it.</para>
///
/// <para><b>Never on marks that stopped applying.</b> The page can change what the marks mean while
/// the confirmation is built or read: a folder made a reference then still holds copies marked in the
/// search role. So the page is asked whether its marks still apply once the confirmation is built and
/// again once the user has answered, and nothing is asked or removed where they do not.</para>
///
/// <para><b>Never while the search runs.</b> The search adds its groups on the page's thread, and
/// everything here reads them on another, so each refuses marks whose search has not ended
/// (<see cref="DuplicateMarks.Complete"/>) before it leaves the calling thread.</para>
/// </summary>
public sealed class DuplicateActions
{
    private readonly Func<CancellationToken, Task<MachineProtections>> _protections;
    private readonly Func<LocalVolume, RecycleBinRoom?> _room;
    private readonly Func<IDuplicateConfirmationPrompt> _prompt;
    private readonly DuplicateRemover _remover;
    private readonly RunningActions _running;

    /// <param name="protections">What this machine protects, built afresh at each call, as <see cref="MachineProtections.ForThisMachineAsync"/> does.</param>
    /// <param name="room">The room in a volume's Recycle Bin, or null where Windows would not say.</param>
    /// <param name="prompt">
    /// The surface that asks. A factory, because the page's dialog needs the window it is shown over,
    /// which does not exist when the page is built.
    /// </param>
    /// <param name="running">What is changing the machine on every page.</param>
    public DuplicateActions(
        Func<CancellationToken, Task<MachineProtections>> protections,
        Func<LocalVolume, RecycleBinRoom?> room,
        Func<IDuplicateConfirmationPrompt> prompt,
        DuplicateRemover remover,
        RunningActions running)
    {
        _protections = protections;
        _room = room;
        _prompt = prompt;
        _remover = remover;
        _running = running;
    }

    /// <summary>The actions on this machine: its protections, its Recycle Bins and the shell's removal.</summary>
    public static DuplicateActions ForThisMachine(Func<IDuplicateConfirmationPrompt> prompt, RunningActions running) =>
        new(MachineProtections.ForThisMachineAsync, RecycleBinRooms.Default.Of, prompt, DuplicateRemover.Default, running);

    /// <summary>Run <paramref name="rule"/> over <paramref name="marks"/>, off the calling thread, since a folder rule opens the folder.</summary>
    /// <param name="ct">Stops the rule between groups, keeping the marks it made before.</param>
    /// <exception cref="InvalidOperationException">The search that made the marks has not ended, or a removal has begun on them.</exception>
    public Task<RuleOutcome> RunAsync(DuplicateMarks marks, MarkingRule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(rule);
        marks.ThrowUnlessOpen();

        return Task.Run(() => marks.Run(rule, ct), ct);
    }

    /// <summary>
    /// Confirm the marks that stand in <paramref name="marks"/>, judged against the machine as it is
    /// now, ask the user, and remove them the way <paramref name="mode"/> says if they say yes. Call it
    /// on the page's thread, where the running action is recorded.
    /// </summary>
    /// <param name="whyNotNow">
    /// Why the marks no longer apply to what the page shows, or null where they still do, asked on the
    /// calling thread.
    /// </param>
    /// <param name="ct">
    /// Stops the removal at the next copy. A removal that has begun still reports what it did and
    /// verifies it.
    /// </param>
    /// <exception cref="InvalidOperationException">The search that made the marks has not ended, or a removal has begun on them.</exception>
    public async Task<DuplicateRemovalAnswer> RemoveAsync(
        DuplicateMarks marks, ExploreRemovalMode mode, Func<string?> whyNotNow, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(whyNotNow);
        marks.ThrowUnlessOpen();

        // Off the page's thread: the judgement reads the registry, every provider and each drive's disks.
        var confirmation = await Task.Run(
            async () => await RemovalConfirmation.ForAsync(
                marks, await _protections(ct).ConfigureAwait(false), mode, _room, _remover.WhyTheBinCannotTake, ct).ConfigureAwait(false),
            ct).ConfigureAwait(true);

        if (whyNotNow() is { } before)
        {
            return new DuplicateRemovalAnswer(confirmation, Report: null, before);
        }

        if (confirmation.Copies.Count == 0 || !await _prompt().AskAsync(confirmation, ct).ConfigureAwait(true))
        {
            return new DuplicateRemovalAnswer(confirmation, Report: null);
        }

        if (whyNotNow() is { } after)
        {
            return new DuplicateRemovalAnswer(confirmation, Report: null, after);
        }

        using var running = _running.Begin(RunningAction.DuplicateRemoval);

        // Not cancelled by the token here: a removal the user confirmed runs to its report, and the
        // remover stops at the next copy when the token asks.
        var report = await Task.Run(
            async () => await _remover.RemoveAsync(
                marks, confirmation, await _protections(CancellationToken.None).ConfigureAwait(false), ct).ConfigureAwait(false),
            CancellationToken.None).ConfigureAwait(true);

        return new DuplicateRemovalAnswer(confirmation, report);
    }
}
