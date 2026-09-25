namespace Deguffer.Core.Execution;

/// <summary>
/// The evidence a deletion was offered on that a program can overturn between the preview and the
/// clean, asked again by the run immediately before the step (§5.3).
///
/// <para><b>Why the plan's own answer is not enough.</b> A provider decides at Preview that nothing is
/// using a folder: the editor has closed, the session has ended, no build is working in the project.
/// A preview can then sit on screen for as long as the user likes, and anything may start in that
/// time. A session resumed, or an IDE opened on the project, makes the plan's answer wrong in the
/// direction that deletes. So the step carries the question as well as the answer, and the run asks
/// it again.</para>
///
/// <para><b>Asked of the machine at the moment of the call.</b> An implementation reads afresh rather
/// than from anything a planning pass memoised, because a remembered "nothing is using it" ages in
/// exactly the direction this exists to stop. It answers by the same rules the plan was built on, so
/// a step is held back at the clean for no reason it would not have been left out at the preview.</para>
///
/// <para>Implemented by each provider whose offer rests on such evidence, so the run holds no
/// knowledge of any tool: it knows only that a step may be held, and what to do with the answer.</para>
/// </summary>
public interface IUseCheck
{
    /// <summary>
    /// What is in use now at or below <paramref name="step"/>'s path, by the rules the plan was built
    /// on. Empty where nothing is, and the step runs as planned.
    ///
    /// <para>An entry at the step's own path holds the whole step back. An entry below it spares that
    /// entry where the step empties a folder in place, and holds the whole step back otherwise,
    /// because a tree something is working in cannot be removed around it.</para>
    ///
    /// <para>Where the rules could not be applied and the plan would have refused, the answer names
    /// the step's own path with that reason: a question that could not be asked is not a clear
    /// answer.</para>
    /// </summary>
    IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct);
}

/// <summary>One place a use check found in use, and why it must stay.</summary>
/// <param name="Path">The place, in display form.</param>
/// <param name="Reason">
/// What is using it, written for the user as a lower-case clause with no full stop, so that it
/// completes both "Nothing was removed: …" and "Left alone because …".
/// </param>
public readonly record struct InUseNow(string Path, string Reason);
