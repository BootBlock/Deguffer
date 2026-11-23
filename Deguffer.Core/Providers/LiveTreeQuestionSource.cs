using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A provider's rule for its <see cref="LiveTreeQuestion"/>, and the question it built for the
/// inspector's current reading, shared by every <see cref="LiveTreeCheck"/> one veto handed out.
///
/// <para><b>Built once per reading, because every check within a reading would build the same
/// one.</b> The question is read from the machine and from the project, so it is never kept past the
/// reading it was built from (see <see cref="LiveTreeQuestion"/>). Within that reading it is a
/// function of what the reading holds: <c>obj</c>'s lists every folder a program is in and parses the
/// solutions there, and a clean of hundreds of <c>obj</c> folders would do that hundreds of times to
/// get one answer. A new reading, taken once the last is past <see cref="IUseCheck.ReadingLimit"/>,
/// builds a new question, so what the project says now is read again within the same bound as the
/// process table.</para>
///
/// <para><b>Only the build is shared.</b> <see cref="LiveTreeQuestion.Ask"/> runs for every check, so a
/// fact a rule reads per directory, such as Unreal's logs or a staging directory's last write, is
/// read again immediately before each removal.</para>
///
/// <para>Kept against the reading named before the build, so a reading dropped by another check while
/// this one builds leaves the question at least as new as the reading it is kept for, never older.</para>
/// </summary>
/// <param name="rule">The provider's rule, applied once for each reading a check asks from.</param>
internal sealed class LiveTreeQuestionSource(Func<CancellationToken, Task<LiveTreeQuestion>> rule)
{
    private readonly Lock _gate = new();
    private (LiveTreeReading Reading, Task<LiveTreeQuestion> Question)? _built;

    /// <summary>
    /// The question for <paramref name="reading"/>, built now where it was not built for it already.
    ///
    /// <para>Checks asking at once from a new reading await the one build between them. The gate only
    /// hands the build out: the rule runs, and is awaited, outside it, so no check is held behind a
    /// build that reads the machine. A build that failed is forgotten, so the next check builds again,
    /// and one cancelled for the check that started it is built again for a check that still wants
    /// the answer.</para>
    /// </summary>
    public async Task<LiveTreeQuestion> ForAsync(LiveTreeReading reading, CancellationToken ct)
    {
        var (question, _) = await SharedTask.AwaitAsync(() => (Take(reading, ct), reading), Forget, ct).ConfigureAwait(false);
        return question;
    }

    /// <summary>
    /// The build for <paramref name="reading"/>, started where none is held for it.
    /// </summary>
    private Task<LiveTreeQuestion> Take(LiveTreeReading reading, CancellationToken ct)
    {
        Task<Task<LiveTreeQuestion>>? start = null;
        Task<LiveTreeQuestion> question;

        lock (_gate)
        {
            if (_built is { } built && built.Reading == reading)
            {
                question = built.Question;
            }
            else
            {
                // Created cold and run below, so the rule's first steps, which may read the disk before
                // it first awaits, are taken outside the gate too. Awaited inside its own async body, so
                // a rule stopped before its first await ends cancelled, as one stopped later does,
                // rather than faulted.
                start = new Task<Task<LiveTreeQuestion>>(async () => await rule(ct).ConfigureAwait(false));
                question = start.Unwrap();
                _built = (reading, question);
            }
        }

        start?.RunSynchronously(TaskScheduler.Default);
        return question;
    }

    private void Forget(Task<LiveTreeQuestion> failed)
    {
        lock (_gate)
        {
            if (_built is { } built && ReferenceEquals(built.Question, failed))
            {
                _built = null;
            }
        }
    }
}
