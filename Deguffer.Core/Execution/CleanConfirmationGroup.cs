using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>The rows of a <see cref="CleanConfirmation"/> that share a tier, in the order they were selected.</summary>
public sealed record CleanConfirmationGroup(SafetyTier Tier, IReadOnlyList<CleanConfirmationItem> Items)
{
    public string Heading => Tier.ToDisplayName();

    /// <summary>
    /// The sentence that holds for every row in the group, which Tier 1 alone has: its rows carry no
    /// consequence of their own (<see cref="CleanConfirmationItem.Consequence"/>), and every other row
    /// states its own.
    /// </summary>
    public string? Note => Tier == SafetyTier.RegenerableCache
        ? "Each of these is rebuilt the next time the tool that owns it runs, so nothing is lost. That first run may be slower."
        : null;
}
