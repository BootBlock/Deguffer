namespace Deguffer.Core.Safety;

/// <summary>
/// The core classification from §3 of the specification. Sizes are easy to compute; this
/// classification is the part that takes knowledge, and it is the product.
/// </summary>
public enum SafetyTier
{
    /// <summary>
    /// Tier 1 — regenerable cache. A tool re-creates it on demand, byte-for-byte or
    /// equivalently. Deleting costs a slower next build; nothing is lost.
    /// </summary>
    RegenerableCache = 1,

    /// <summary>
    /// Tier 2 — regenerable, with cost. Re-created only by re-downloading gigabytes or
    /// re-indexing for minutes. Offered, but never pre-selected.
    /// </summary>
    RegenerableWithCost = 2,

    /// <summary>
    /// Tier 3 — user data wearing a cache costume. Logs, histories, saved sessions.
    /// Deleting loses it permanently.
    /// </summary>
    UserData = 3,

    /// <summary>
    /// Tier 4 — do not touch. Config, credentials, live application state, or anything the
    /// tool cannot prove is idle. Excluded from the UI entirely.
    /// </summary>
    DoNotTouch = 4,
}

public static class SafetyTierExtensions
{
    /// <summary>Whether a tier may be pre-selected for the user (§3, "Default" column).</summary>
    public static bool IsPreSelectedByDefault(this SafetyTier tier) => tier == SafetyTier.RegenerableCache;

    /// <summary>Whether a tier may be offered for deletion at all.</summary>
    public static bool IsOfferable(this SafetyTier tier) => tier != SafetyTier.DoNotTouch;

    /// <summary>Whether removing this tier destroys something irreplaceable.</summary>
    public static bool IsIrreversibleLoss(this SafetyTier tier) => tier == SafetyTier.UserData;

    /// <summary>Short label for the UI.</summary>
    public static string ToDisplayName(this SafetyTier tier) => tier switch
    {
        SafetyTier.RegenerableCache => "Regenerable cache",
        SafetyTier.RegenerableWithCost => "Regenerable, with cost",
        SafetyTier.UserData => "User data",
        SafetyTier.DoNotTouch => "Do not touch",
        _ => tier.ToString(),
    };
}
