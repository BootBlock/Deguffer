using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// The one sentence for a directory Deguffer was not allowed to list.
///
/// <para>Written once because it is one fact, and because a hand-written copy is where a fact like
/// this goes missing — which is what <see cref="Safety.ChildDirectories"/> itself records happening
/// to the two rules it was extracted to hold. Every provider that enumerates a root can meet this,
/// and the answer is the same everywhere: name the folder, say nothing inside it was examined, and
/// do not let the figures imply otherwise.</para>
///
/// <para>A warning rather than information. A link a provider declined is something Deguffer looked
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
}
