namespace Deguffer.Core.Execution;

/// <summary>
/// What a provider's steps are to the user: parts of one location decided about together, or separate
/// items recognised and chosen one by one.
///
/// <para><b>Declared, because the step count cannot say it.</b> Forty steps and forty things a user
/// recognises are different facts. A browser's cache folders across three profiles are a dozen steps
/// nobody chooses between by name, and one superseded application build is a single step the user
/// still has to see named before it goes. Reading the grain off the count got the second case wrong
/// in the direction that matters: a single step is the whole row, so the one item was never listed
/// as an item at all.</para>
///
/// <para><b>It decides what the shell offers, never what is deleted.</b> Whatever the grain, a run is
/// the plan narrowed through <see cref="CleanupPlan.NarrowedTo"/>, so every step the user leaves out is
/// protected and checked afterwards (§5.6).</para>
/// </summary>
public enum StepGrain
{
    /// <summary>
    /// The steps are parts of one location, and the row is the decision. Where there is more than one
    /// part, each can still be left out: that is what lets one vendor's shader cache or a tool's
    /// scratch folder go while the rest stays.
    /// </summary>
    Parts,

    /// <summary>
    /// Each step is a thing the user recognises on its own: a project's build output, a browser build,
    /// an application's superseded version. Each is listed and chosen separately, including when there
    /// is only one.
    /// </summary>
    Items,
}

public static class StepGrainExtensions
{
    /// <summary>
    /// Whether each of a plan's <paramref name="stepCount"/> steps is offered for choice on its own:
    /// every item, and a part only where there is more than one. A single part is the whole row, and a
    /// checkbox against it as well as against the row would put two controls on screen for one
    /// decision.
    /// </summary>
    public static bool OffersEachStep(this StepGrain grain, int stepCount) =>
        grain == StepGrain.Items ? stepCount > 0 : stepCount > 1;
}
