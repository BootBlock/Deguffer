using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// The sentences for a directory Windows would not let Deguffer see into, in the two shapes that
/// refusal takes.
///
/// <para>Written once because each is one fact, and because a hand-written copy is where a fact like
/// this goes missing — which is what <see cref="Safety.ChildDirectories"/> itself records happening
/// to the two rules it was extracted to hold. Every provider that enumerates a root can meet this,
/// and the answer is the same everywhere: name the folder, say nothing inside it was examined, and
/// do not let the figures imply otherwise.</para>
///
/// <para><b>The two shapes differ in what they establish, so they may not share a sentence.</b> A
/// root that will not be <em>listed</em> was reached, so it is certainly there and only its contents
/// are unknown. A root a probe by name could not reach is not even known to exist, so a sentence
/// saying Deguffer could not list it would assert something nobody established. See
/// <see cref="Safety.PathPresence"/>.</para>
///
/// <para>Warnings rather than information. A link a provider declined is something Deguffer looked
/// at and decided about; this is something it never saw, so the plan beside it is incomplete by an
/// amount nobody can state.</para>
/// </summary>
internal static class UnreadableRoot
{
    public static PlanNote Note(string root) => new(
        PlanNoteSeverity.Warning,
        $"Deguffer could not list '{root}', so nothing inside it was examined. Anything in there is "
        + "left alone and is not counted in the size shown.");

    /// <summary>
    /// The sentence for a provider whose whole plan came to nothing because its root would not be
    /// listed. It replaces the "there is nothing here" sentence such a provider used to emit, which
    /// its own presence probe had already contradicted — a probe by full name answers through a
    /// directory the account may not list, because traversing and listing are separate rights.
    /// </summary>
    public static string WhyNothingWasPlanned(string root) =>
        $"Deguffer could not list '{root}', so it could not work out what is in there. Nothing was "
        + "planned, and nothing was ruled out either.";

    /// <summary>
    /// The note for a root <see cref="Safety.LongPath.ProbeDirectory"/> was refused, where the
    /// provider carries on with the rest of what it examines.
    /// </summary>
    public static PlanNote UnreachedNote(string root) => new(
        PlanNoteSeverity.Warning,
        WhyItCouldNotBeReached(root));

    /// <summary>
    /// The sentence for a root Windows would not describe at all, so Deguffer cannot say whether it
    /// is there, let alone what is in it.
    ///
    /// <para>It names the causes rather than one of them, because they send the reader to different
    /// places and none is guessable from the others. An access rule is the user's to change. A link
    /// Windows declines to follow is not, and neither is a drive that is not connected: relocating a
    /// cache onto another drive is a thing developers do on purpose, and there are no permissions to
    /// fix. The list is deliberately not exhaustive, because the probe cannot tell which of them it
    /// met.</para>
    ///
    /// <para>What it must never say is that the location is absent, which is what the two-state
    /// probe this replaced made every such provider say about a cache that was on the disk.</para>
    /// </summary>
    public static string WhyItCouldNotBeReached(string root) =>
        $"Windows would not say what is at '{root}', so Deguffer could not look inside it. A link it "
        + "will not follow, a folder this account may not read and a drive that is not connected all "
        + "do that. Nothing was planned, and nothing was ruled out either.";
}
