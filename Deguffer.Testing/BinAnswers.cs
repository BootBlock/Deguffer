using Deguffer.Core.Duplicates;

namespace Deguffer.Testing;

/// <summary>
/// Answers to the question a duplicate removal's confirmation asks of the Recycle Bin: which of the
/// copies that stand it cannot take, in the same order.
/// </summary>
public static class BinAnswers
{
    /// <summary>A bin that can take every copy it is asked about.</summary>
    public static IReadOnlyList<string?> TakesEvery(IReadOnlyList<DuplicateCandidate> copies) => new string?[copies.Count];
}
