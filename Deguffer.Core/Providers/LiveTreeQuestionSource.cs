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
internal sealed class LiveTreeQuestionSource(Func<CancellationToken, LiveTreeQuestion> rule)
{
    private readonly Lock _gate = new();
    private (LiveTreeReading Reading, LiveTreeQuestion Question)? _built;

    /// <summary>The question for <paramref name="reading"/>, built now where it was not built for it already.</summary>
    public LiveTreeQuestion For(LiveTreeReading reading, CancellationToken ct)
    {
        // Built under the gate, so checks asking at once from a new reading build it once between them.
        lock (_gate)
        {
            if (_built is { } built && built.Reading == reading)
            {
                return built.Question;
            }

            var question = rule(ct);
            _built = (reading, question);
            return question;
        }
    }
}
