using Deguffer.Core.Safety;

namespace Deguffer.App.Shell;

/// <summary>
/// One of §3's tiers as About states it, in the words a Storage row's badge and its dialogs use.
/// </summary>
/// <param name="Tier">The tier, which picks the chip's colour.</param>
/// <param name="Name">Its <see cref="SafetyTierExtensions.ToDisplayName"/>.</param>
/// <param name="Explanation">Its <see cref="SafetyTierExtensions.ToExplanation"/>.</param>
public sealed record TierStatement(SafetyTier Tier, string Name, string Explanation);
