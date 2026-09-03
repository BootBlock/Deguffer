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
public sealed record ProtectedPath(string Path, string Reason, bool ExistedBefore);

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
    /// <para>Recomputed rather than stored, like <see cref="TargetedPaths"/>: this is a record, and
    /// a <c>with</c> expression copies backing fields wholesale, so a cached value would survive a
    /// change to <see cref="Steps"/> and describe the wrong plan.</para>
    /// </summary>
    public bool HasRecentContentHeldBack => Steps.Any(s => s.WithheldRecent);

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

    /// <summary>A plan with no steps is a no-op — the toolchain is absent, or already clean.</summary>
    public bool IsEmpty => Steps.Count == 0;

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

        // Named for the user's choice rather than for what is kept, because "keep" now means the
        // guard on recently changed files everywhere else in this project, and the two decide
        // different things about the same plan.
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
                ExistedBefore: true));

        return this with
        {
            Steps = selected,
            ProtectedPaths = [.. ProtectedPaths, .. declined],
        };
    }
}
