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
/// <para><b>Asked of the machine as it is now, to within <see cref="ReadingLimit"/>.</b> An
/// implementation never answers from a reading older than that, because a remembered "nothing is
/// using it" ages in exactly the direction this exists to stop. It answers by the same rules the plan
/// was built on, so a step is held back at the clean for no reason it would not have been left out at
/// the preview, save one: the clean could not ask what the preview could.</para>
///
/// <para>Implemented by each provider whose offer rests on such evidence, so the run holds no
/// knowledge of any tool: it knows only that a step may be held, and what to do with the answer.</para>
/// </summary>
public interface IUseCheck
{
    /// <summary>
    /// The oldest a reading of the running programs may be when a check asks for it, measured from
    /// when the reading began. The reading may be the preview's own, where the preview's began less
    /// than this long before the check.
    ///
    /// <para><b>Why a bound rather than a reading per check.</b> A clean of a thousand build
    /// directories is a thousand steps, each with its check, and one reading of the process table
    /// opens every process on the machine and reads its environment block and command line: tens of
    /// milliseconds, so a reading per check added tens of seconds to the run. Within the bound, every
    /// check answers from one reading, and a run reads the table at most once a second however many
    /// steps it has.</para>
    ///
    /// <para><b>Why a second is fresh enough.</b> The check is a look at one moment, and the removal
    /// after it takes as long as the tree takes: a program started a moment after the look is missed
    /// however fresh the look was. A second widens that by less than the removal of one large tree
    /// already does. The preview's reading began before the preview finished, so to start a program
    /// that a check answering from it misses, someone has to read the preview, open an editor or
    /// start a build, and press Clean, all within a second of that reading.</para>
    ///
    /// <para><b>What the bound applies to.</b> Every change to the programs running, a program a command
    /// step started included. No step that removes a folder starts or ends a program, so a removal
    /// is no reason to read again.</para>
    /// </summary>
    public static readonly TimeSpan ReadingLimit = TimeSpan.FromSeconds(1);

    /// <summary>
    /// What is in use now at or below <paramref name="step"/>'s path, by the rules the plan was built
    /// on. Empty where nothing is, and the step runs as planned.
    ///
    /// <para>An entry at the step's own path holds the whole step back. An entry below it spares that
    /// entry where the step empties a folder in place, and holds the whole step back otherwise,
    /// because a tree something is working in cannot be removed around it.</para>
    ///
    /// <para>Where the question cannot be asked in full now and the plan's answer was whole, the answer
    /// names the step's own path with that reason: a question that could not be asked is not a clear
    /// answer, and the step was offered on one that could. A check whose question can come back partial
    /// remembers which answer its plan had, because only the check can. Where the plan's answer was
    /// partial too, it offered the step on that answer and said so in a note, and the step runs as
    /// planned.</para>
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
