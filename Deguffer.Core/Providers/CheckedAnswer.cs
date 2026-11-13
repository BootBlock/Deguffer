using System.Diagnostics.CodeAnalysis;
using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// A tool's answer a plan waits on, asked only once every check the plan makes before asking had
/// passed, or the plan the first check to refuse returns in its place.
///
/// <para><b>Why the checks are remembered with the answer.</b> A provider that starts its question as
/// the pass begins (<see cref="ICleanupProvider.BeginPass"/>) has its plan await the same remembered
/// answer. The checks before the question are disk work: a probe of the folder the tool is asked
/// about, and a listing the §5.6 negative is later read from. Made again by the plan they would ask
/// the disk twice, and two listings taken apart could describe two different folders, one the tool
/// was asked about and one the plan protects. Remembered together, the pass has one reading.</para>
/// </summary>
/// <typeparam name="T">What the plan is built from once every check has passed.</typeparam>
internal sealed class CheckedAnswer<T>
    where T : class
{
    private readonly CleanupPlan? _refusal;
    private readonly T? _answer;

    private CheckedAnswer(CleanupPlan? refusal, T? answer)
    {
        _refusal = refusal;
        _answer = answer;
    }

    public static implicit operator CheckedAnswer<T>(CleanupPlan refusal) => new(refusal, null);

    public static implicit operator CheckedAnswer<T>(T answer) => new(null, answer);

    /// <summary>Whether a check refused, with the plan it returns; otherwise the answer.</summary>
    public bool Refused([NotNullWhen(true)] out CleanupPlan? plan, [NotNullWhen(false)] out T? answer)
    {
        plan = _refusal;
        answer = _answer;

        return plan is not null;
    }
}
