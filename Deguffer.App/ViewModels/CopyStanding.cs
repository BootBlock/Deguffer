using Deguffer.Core.Duplicates;

namespace Deguffer.App.ViewModels;

/// <summary>Why a copy may not be marked and why it may not be kept, as Core judged them, ready for its row.</summary>
public sealed record CopyStanding(string WhyNotMarked, string WhyNotKept)
{
    /// <summary>What <paramref name="keeping"/> says of <paramref name="copy"/>. Asks the policy, so call it where the marks are read.</summary>
    public static CopyStanding Of(DuplicateCandidate copy, CopyKeeping keeping)
    {
        var notMarked = CopyRefusals.WhyNeverMarked(copy) ?? keeping.Refusals.WhyRefused(copy);
        var notKept = keeping.WhyNotKept(copy);

        return new CopyStanding(notMarked ?? string.Empty, notKept is null || notKept == notMarked ? string.Empty : notKept);
    }
}
