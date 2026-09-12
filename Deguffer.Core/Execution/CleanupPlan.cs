using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// A path the plan asserts will still be there afterwards (§5.6). Verifying the negative is
/// cheap, and it catches an over-broad rule on the first run rather than the hundredth.
/// </summary>
/// <param name="Path">The path that must survive.</param>
/// <param name="Reason">Why it matters — shown to the user in the verification report.</param>
/// <param name="ExistedBefore">
/// Whether it was present when the plan was made. A path that was never there cannot have been
/// destroyed, so only the ones that existed constitute evidence.
/// </param>
/// <param name="HeldContentBefore">
/// Whether it was a directory with content anywhere below it when the plan was made: a file, a link,
/// or a directory that would not be listed. A folder holding only empty folders does not count; see
/// <see cref="DirectoryContent"/> for why.
///
/// <para><b>Existence stopped being enough when a removal stopped removing.</b> Every deletion in
/// this product used to take the directory with it, so an over-broad rule made a protected sibling
/// vanish and <see cref="PlanVerifier"/>'s existence check saw it. A route that empties a directory
/// <em>in place</em> — <see cref="EmptyRecycleBinStep"/> is the first — destroys the contents and
/// leaves the directory standing, so the same over-reach leaves every protected path present and
/// the negative passes over a bin somebody else's deleted files were in. That is the exact failure
/// §5.6 exists to catch, and it is invisible to a question about existence.</para>
///
/// <para>Recorded as a boolean rather than a size or a count, because the claim worth holding the
/// run to is "it still holds something", not "it holds the same bytes". A protected directory can
/// legitimately gain or lose an entry between the preview and the clean, and a comparison of
/// figures would report every one of those as an alarm.</para>
/// </param>
/// <param name="Withheld">
/// Whether this path is something the plan would have offered and a choice took out, rather than
/// something a rule protects.
///
/// <para>It is what lets a row with nothing to reclaim say why. A path a rule protects — a tool
/// root, an unrecognised sibling, an entry a running program is working in — was never going to be
/// offered, and it leaves the row's zero honest. A withheld candidate is the opposite case:
/// something real is there, Deguffer would have offered it, and the figure excludes it, so a row
/// holding one is not clear. Nothing else on the plan can say so once the step is gone.</para>
/// </param>
public sealed record ProtectedPath(
    string Path,
    string Reason,
    bool ExistedBefore,
    bool HeldContentBefore = false,
    Withholding Withheld = Withholding.None);

/// <summary>Which choice, if any, took a candidate out of a plan rather than a rule keeping it out.</summary>
public enum Withholding
{
    /// <summary>
    /// Not a withheld candidate. A rule protects it, or the user left it unticked for one run, which
    /// is a narrowing of what runs rather than a statement about the row.
    /// </summary>
    None,

    /// <summary>
    /// A file the guard on recently changed files withdrew whole, because it changed inside the
    /// window and the step had nothing else to do.
    /// </summary>
    TooRecent,

    /// <summary>
    /// An item on the user's keep list, protected on its existence alone. See
    /// <see cref="CleanupPlan.WithKeepList"/> for why its contents are not asked about.
    /// </summary>
    OnKeepList,

    /// <summary>
    /// An Outlook mail store the plan's own measurement found inside what it would remove, which
    /// Deguffer never removes (§9). Protected on its existence, because it is a file. See
    /// <see cref="MailStorePlan"/>.
    /// </summary>
    MailStore,
}

/// <summary>A remark attached to a plan: something the user should know before confirming.</summary>
public sealed record PlanNote(PlanNoteSeverity Severity, string Message);

public enum PlanNoteSeverity
{
    Information,
    Warning,
}

/// <summary>
/// Exactly what would happen, computed but never executed. §7: the dry run is the default
/// action, so this is the object the primary button produces.
/// </summary>
public sealed record CleanupPlan
{
    public required string ProviderId { get; init; }

    /// <summary>The named cause, not the path — "Gradle build cache" (§2).</summary>
    public required string ProviderName { get; init; }

    public required SafetyTier Tier { get; init; }

    /// <summary>§7: every row states what happens on next use.</summary>
    public required string WhatHappensOnNextUse { get; init; }

    public IReadOnlyList<CleanupStep> Steps { get; init; } = [];

    public IReadOnlyList<ProtectedPath> ProtectedPaths { get; init; } = [];

    public IReadOnlyList<PlanNote> Notes { get; init; } = [];

    /// <summary>
    /// The user's guard on recently touched files, fixed when this plan was made.
    ///
    /// <para>It travels on the plan rather than being read again at execution, and that is what
    /// makes the preview a promise. The cut-off is an instant (see <see cref="MinimumAge"/>), so
    /// the set of files it protects is the same set the estimate above excluded, however long the
    /// preview sits on screen before the user presses Clean. Re-deriving it from the clock at
    /// deletion would quietly delete files the preview said would stay.</para>
    ///
    /// <para>Stamped by <see cref="Providers.CleanupProviderBase.PlanAsync"/> on every plan a
    /// provider returns, rather than by each provider, so a provider that builds its plan by hand
    /// cannot ship one the executor would treat as unguarded.</para>
    /// </summary>
    public MinimumAge Keep { get; init; }

    /// <summary>
    /// Whether the guard left something real out of this plan's figures.
    ///
    /// <para>The same shape as <see cref="HasUnreadableRoot"/>, and there for the same reason: a
    /// row with nothing to reclaim renders as "Already clear", and that is a claim about the
    /// folder. A cache whose every file is inside the window measures zero and is full, so the
    /// claim would be false. Deriving it from <see cref="Keep"/> alone is wrong in the other
    /// direction — it puts "nothing old enough" on every genuinely empty row the moment the user
    /// switches the guard on, which on an ordinary machine is most of them.</para>
    ///
    /// <para><b>The guard holds something back in two shapes, and both are asked.</b> A directory
    /// stays a step and does less, and says so through <see cref="CleanupStep.WithheldRecent"/>. A
    /// file whose whole subject is recent is withdrawn, which leaves no step to say anything — only
    /// the protection that proves it survived. Asking the steps alone put "Already clear" on a row
    /// whose one crash dump was still on the disk.</para>
    ///
    /// <para>Recomputed rather than stored, like <see cref="TargetedPaths"/>: this is a record, and
    /// a <c>with</c> expression copies backing fields wholesale, so a cached value would survive a
    /// change to <see cref="Steps"/> and describe the wrong plan.</para>
    /// </summary>
    public bool HasRecentContentHeldBack =>
        Steps.Any(s => s.WithheldRecent)
        || ProtectedPaths.Any(p => p.Withheld == Withholding.TooRecent);

    /// <summary>
    /// Whether this plan's figures leave out something Windows would not let a previous clean take,
    /// and still would not when this plan was made. The same shape as
    /// <see cref="HasRecentContentHeldBack"/>, for the same reason: a row whose every byte is refused
    /// measures zero and is not clear. Recomputed rather than stored, for the reason given there.
    /// </summary>
    public bool HasRefusedContent => Steps.Any(s => !s.Refused.IsEmpty);

    /// <summary>
    /// Which route measured this plan's paths. <see cref="FallbackReason.None"/> for a plan with
    /// nothing to measure, which is correct: an empty plan gives the user no reason to elevate.
    ///
    /// The matching sentence is already in <see cref="Notes"/>; this is the same fact in a form the
    /// UI can act on, because "would elevating help here?" is a decision, not a sentence.
    /// </summary>
    public FallbackReason Fallback { get; init; } = FallbackReason.None;

    /// <summary>
    /// Whether a directory this plan describes refused to be listed, so what is inside it was never
    /// examined and nothing below it is in the figures.
    ///
    /// <para>The same shape as <see cref="Fallback"/>, and for the same reason: the sentence is
    /// already in <see cref="Notes"/>, and this is that fact in the form the shell can act on,
    /// because "is this row actually clear?" is a decision rather than a sentence. A present row
    /// with nothing to reclaim renders as "Already clear", and that is a claim — it must not be
    /// made about a folder nobody was allowed to read.</para>
    ///
    /// <para>A refusal is ordinary rather than an error (§5.3), and a listing right is separate from
    /// a traverse right — so a provider that probed for its cache <em>by name</em> can find it there
    /// and then be refused the listing that would classify its children. Four providers reported
    /// that combination as "there is nothing here", contradicting their own presence probe within
    /// one planning pass.</para>
    /// </summary>
    public bool HasUnreadableRoot { get; init; }

    /// <summary>
    /// Whether this plan's zero fails to describe its subject, because Deguffer would not look
    /// through part of it and found nothing to offer in the rest.
    ///
    /// <para>The counterpart to <see cref="HasUnreadableRoot"/>, and deliberately not the same flag:
    /// the two send the reader to different places. That one is Windows refusing a listing, and the
    /// answer to it is permissions. This one is Deguffer's own decision — a root that turned out to
    /// be a link, or a tool that would not say where its cache is — and there are no permissions to
    /// check. Relocating a cache onto another drive with a junction is a thing developers do on
    /// purpose, so "could not be read" would send one hunting a fault that is not there.</para>
    ///
    /// <para>What it shares with that flag is the reason it exists at all: a present row with
    /// nothing to reclaim renders as "Already clear", and that is a claim about the location. It is
    /// no more true of a folder nobody looked in than of one nobody was allowed to read, and the
    /// junctioned root behind it is routinely the largest thing this tool would have found.</para>
    ///
    /// <para><b>The test is "nothing targeted, and something declined", never "nothing examined".</b>
    /// A provider reaches this state by two routes, and only the first one examines nothing at all:
    /// an early return before it looked (a junctioned root, a tool that would not name its cache),
    /// or a full pass that classified what it could and declined every candidate it would have
    /// offered. Both leave a figure of zero that excludes an amount nobody can state, which is the
    /// thing the row must not present as clear. The second route is why the condition does not ask
    /// whether examining happened: a folder holding one junction and three children Deguffer read
    /// and left alone has been examined, and its zero is still not the whole story.</para>
    ///
    /// <para>Nothing targeted is the load-bearing half. A plan with a step has a size and reads
    /// "Ready to clean", so widening the declined half can never hide a real total. It is asked of
    /// the targets rather than the steps, so that a plan the guard on recently changed files
    /// emptied stays <see cref="HasRecentContentHeldBack"/>'s to describe rather than this one's.
    /// </para>
    /// </summary>
    public bool WasNotExamined { get; init; }

    /// <summary>
    /// Whether any step here cannot be carried out without administrator rights.
    ///
    /// Separate from <see cref="Fallback"/> on purpose: that one is about how a size was arrived at,
    /// and this one is about whether the removal can happen at all. A plan whose sizes came off the
    /// file table quickly can still hold a step nobody unelevated may perform.
    /// </summary>
    public bool RequiresElevation => Steps.Any(s => s.RequiresElevation);

    /// <summary>Total reclaim estimated across all steps.</summary>
    public long EstimatedBytes => Steps.Sum(s => s.EstimatedBytes);

    /// <summary>
    /// The same total with both numbers intact, and with the approximation flag preserved: a plan
    /// holding a step whose figure is a forecast rather than a measurement is only as exact as that
    /// step. Both of §5.5's routes measure, so neither sets the flag; conda's dry run and the
    /// sole-link sum do.
    /// </summary>
    public ScanSize Estimated => Steps.Aggregate(ScanSize.Zero, (total, step) => total + step.Estimated);

    /// <summary>
    /// What choosing every step here reclaims, as the shell states it. See
    /// <see cref="CleanupStep.Reclaim"/> for how that differs from <see cref="Estimated"/>.
    /// </summary>
    public ScanSize Reclaim => Steps.Aggregate(ScanSize.Zero, (total, step) => total + step.Reclaim);

    /// <summary>
    /// A plan with no steps removes nothing: the toolchain is absent, the location is already clean,
    /// or every candidate it found was withheld. Whether it still has something to verify is
    /// <see cref="HasSomethingToProve"/>'s question, and it is a different one.
    /// </summary>
    public bool IsEmpty => Steps.Count == 0;

    /// <summary>
    /// Whether running this plan would establish anything even where it removes nothing: a withheld
    /// candidate that was there when the plan was made, so finding it standing afterwards is
    /// evidence.
    ///
    /// <para>Separate from <see cref="IsEmpty"/> because a plan whose every candidate was withheld
    /// is both. It has no steps and it still makes a promise, and a run that dropped it as having
    /// nothing to do left that promise with no evidence behind it — on exactly the run where the
    /// user's instruction was to leave something alone. See <see cref="CleanupPlanner.ExecuteAsync"/>.
    /// </para>
    ///
    /// <para><b>Withheld candidates only, never a path a rule protects.</b> A tool root or an
    /// unrecognised sibling is protected against the plan's own deletion, and a plan with no steps
    /// deletes nothing, so its survival proves nothing about that plan. Counting one would put every
    /// already-clear location with a root on the disk into a run's verdict, named as though it had
    /// been cleaned.</para>
    /// </summary>
    public bool HasSomethingToProve =>
        ProtectedPaths.Any(p => p.ExistedBefore && p.Withheld != Withholding.None);

    /// <summary>
    /// Whether an item this plan found is on the user's keep list, and so was left out of it.
    ///
    /// <para>The same shape as <see cref="HasRecentContentHeldBack"/>, and there for the same reason.
    /// A row whose every item is kept has nothing to reclaim, and "Already clear" would then be a claim
    /// about a location holding exactly what the user asked Deguffer to leave. The sentence is in
    /// <see cref="Notes"/>, and this is the same fact in the form the shell can act on.</para>
    /// </summary>
    public bool HoldsKeepListItems => ProtectedPaths.Any(p => p.Withheld == Withholding.OnKeepList);

    /// <summary>
    /// Whether this plan found an Outlook mail store and is leaving it where it is. The same shape as
    /// <see cref="HoldsKeepListItems"/>, for the same reason: a row whose only content is a store has
    /// nothing to reclaim, and "Already clear" would be a claim about a location holding a file
    /// Deguffer will never remove.
    /// </summary>
    public bool HoldsMailStores => ProtectedPaths.Any(p => p.Withheld == Withholding.MailStore);

    /// <summary>
    /// Every path this plan would destroy, for display and for tests.
    ///
    /// Selected on <see cref="DeleteStep"/> rather than on one concrete kind, so a directory and a
    /// single file both count and a future deletion kind counts without an edit here. A step that
    /// frees space without destroying anything contributes nothing, which is what the cloud-sync
    /// dehydration in <c>docs/todo/unreached-locations.md</c> §10 will need.
    ///
    /// Deliberately not cached in a backing field: this is a record, and a <c>with</c> expression
    /// copies backing fields wholesale, so a cached list would survive a change to
    /// <see cref="Steps"/> and quietly describe the wrong plan. This is the collection the safety
    /// tests assert against, which makes it the last place a stale value is acceptable. Steps
    /// number in the low single digits, so recomputing costs nothing.
    /// </summary>
    public IReadOnlyList<string> TargetedPaths => [.. Steps.OfType<DeleteStep>().Select(s => s.Path)];

    /// <summary>
    /// This plan narrowed to the steps the user actually chose.
    ///
    /// Narrowing lives here rather than in the shell because of what it has to do besides drop
    /// steps: every deletion the user declined becomes a protected path. §5.6's negative is the
    /// promise that a step which did not run left its subject standing, and after per-item
    /// selection the deselected directory is a sibling of the selected one — same parent, same
    /// shape — which is exactly when an over-broad rule takes both. A shell that narrowed a plan by
    /// filtering <see cref="Steps"/> itself would silently drop that guarantee, so the only
    /// narrowing available adds it.
    ///
    /// A dropped <see cref="RunCommandStep"/> contributes no protection: its
    /// <see cref="RunCommandStep.MeasuredPaths"/> are a probe rather than a target (§5.1), and
    /// asserting the tool left them alone would be asserting something this plan never controlled.
    /// </summary>
    public CleanupPlan NarrowedTo(IReadOnlyCollection<CleanupStep> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);

        // Named for the user's choice rather than for what is kept, because "keep" already means the
        // guard on recently changed files and the keep list elsewhere in this project, and each of
        // those decides something different about the same plan.
        var selected = Steps.Where(chosen.Contains).ToList();

        if (selected.Count == Steps.Count)
        {
            return this;
        }

        var declined = Steps
            .Except(selected)
            .OfType<DeleteStep>()
            .Select(s => new ProtectedPath(
                s.Path,
                "Left alone because it was not selected for this run.",
                // It was measured during planning, so it was there when the plan was made. That is
                // the only claim ExistedBefore makes, and re-probing the disk here would let a
                // directory deleted between planning and execution excuse itself.
                ExistedBefore: true,

                // Content is asked of the disk rather than assumed, which is the opposite of the
                // line above and for a reason the two do not share. Assuming it would report every
                // declined directory that was *already* empty as having been emptied by the run —
                // an alarm about an untouched folder, on a machine where an unused drive's bin is
                // ordinary. The excuse the probe allows is harmless in the other direction: a
                // directory something else emptied in the gap has nothing left for an over-broad
                // rule to destroy.
                //
                // This is the site the declined Recycle Bin depends on. A bin the user unticked is
                // still standing after a call that emptied it anyway, so existence proves nothing
                // and this is the whole of what §5.6 has left to compare.
                DirectoryContent.IsPresent(s.Path)));

        return this with
        {
            Steps = selected,
            ProtectedPaths = [.. ProtectedPaths, .. declined],
        };
    }

    /// <summary>
    /// This plan with every item on the keep list taken out of it, and each of those protected instead.
    ///
    /// <para><b>Matched on <see cref="DeleteStep.Identity"/>, never on the path.</b> A path changes
    /// when a cache is relocated or a project is moved, and the item does not. A keep entry matched on
    /// the path would silently stop matching, and the item would be offered again, which is the one
    /// direction a protection must not fail in. A step with no identity is never kept.</para>
    ///
    /// <para><b>Protected on existence alone.</b> <see cref="ProtectedPath.HeldContentBefore"/> lets
    /// §5.6 catch a directory emptied in place, and for a kept item it would catch the wrong thing.
    /// The tool that owns the item goes on working on it: an updater replaces the build beside the one
    /// kept, and a tool that sweeps its own store sweeps a kept item's contents with the rest. A kept
    /// directory emptied between the preview and the clean is then ordinary, and nothing Deguffer did
    /// caused it. An alarm about it would cry wolf about a folder Deguffer never touched, and a §5.6
    /// alarm that cries wolf is worth less than no alarm. What an over-broad rule does to a kept item
    /// is delete it, and existence still catches that.</para>
    ///
    /// <para><b>Applied before <see cref="NarrowedTo"/>.</b> A kept item is no longer a step, so a
    /// shell that chose every step it was shown still cannot run one. Applying this to a plan that
    /// already had it applied changes nothing.</para>
    /// </summary>
    /// <param name="keys">
    /// The keys kept for this plan's provider, as <see cref="Configuration.KeepList.KeysFor"/> gives
    /// them, with that set's own comparison.
    /// </param>
    public CleanupPlan WithKeepList(IReadOnlySet<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var kept = Steps
            .OfType<DeleteStep>()
            .Where(step => step.Identity is { } identity && keys.Contains(identity.Key))
            .ToList();

        if (kept.Count == 0)
        {
            return this;
        }

        return this with
        {
            Steps = [.. Steps.Except(kept)],
            ProtectedPaths =
            [
                .. ProtectedPaths,
                .. kept.Select(step => new ProtectedPath(
                    step.Path,
                    "On your keep list, so Deguffer left it alone.",
                    // Measured during planning, so it was there when the plan was made: the claim
                    // NarrowedTo makes, for the reason it gives.
                    ExistedBefore: true,
                    HeldContentBefore: false,
                    Withheld: Withholding.OnKeepList)),
            ],
            Notes =
            [
                .. Notes,
                new PlanNote(
                    PlanNoteSeverity.Information,
                    kept.Count == 1
                        ? "One item here is on your keep list. Deguffer leaves it alone, and every clean checks "
                          + "that it is still there."
                        : $"{kept.Count} items here are on your keep list. Deguffer leaves them alone, and every "
                          + "clean checks that they are still there."),
            ],
        };
    }

    /// <summary>
    /// This plan reduced to what a run owes its keep list: nothing to do, and the kept items to prove.
    ///
    /// <para>For a row the user did not tick. Nothing in it runs, and its kept items still have to be
    /// shown standing once the run is over, because an over-broad rule somewhere else in that run is
    /// exactly what could take one. Its other protections are left out: they guard against this
    /// plan's own deletion, and there is none.</para>
    /// </summary>
    public CleanupPlan KeepListItemsOnly() => this with
    {
        Steps = [],
        ProtectedPaths = [.. ProtectedPaths.Where(p => p.Withheld == Withholding.OnKeepList)],
    };
}
