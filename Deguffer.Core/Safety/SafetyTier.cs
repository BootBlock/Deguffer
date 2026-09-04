namespace Deguffer.Core.Safety;

/// <summary>
/// The core classification from §3 of the specification. Sizes are easy to compute; this
/// classification is the part that takes knowledge, and it is the product.
/// </summary>
public enum SafetyTier
{
    /// <summary>
    /// Tier 1 — regenerable cache. Whatever produced it re-creates it on demand, byte-for-byte
    /// or equivalently. Deleting costs a slower next use; nothing is lost.
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

    /// <summary>
    /// The headline answer to "should I clean this?", for the reader deciding whether to tick the
    /// row. It is derived from the tier rather than declared per provider, because §3's tier table
    /// is already that decision — a provider free to write its own verdict is a provider free to
    /// recommend cleaning something the table never pre-selects.
    ///
    /// The reasoning under it is the provider's own: see
    /// <see cref="Deguffer.Core.Providers.ProviderDescription.Recommendation"/>.
    /// </summary>
    public static string ToCleaningAdvice(this SafetyTier tier) => tier switch
    {
        SafetyTier.RegenerableCache => "Generally safe to clean",
        SafetyTier.RegenerableWithCost => "Clean it when you need the space",
        SafetyTier.UserData => "Clean it only once you are sure you no longer need what is in it",
        SafetyTier.DoNotTouch => "Never cleaned by Deguffer",
        _ => tier.ToString(),
    };

    /// <summary>
    /// What the tier itself means, for a reader who has met the badge and not the tier table. The
    /// About page states all four together; a badge on a row states only its own, so both read it
    /// from here rather than each carrying its own wording.
    /// </summary>
    public static string ToExplanation(this SafetyTier tier) => tier switch
    {
        SafetyTier.RegenerableCache =>
            "Whatever made it re-creates it on demand. Deleting costs a slower next use and " +
            "nothing else, so these are selected for you by default.",
        SafetyTier.RegenerableWithCost =>
            "Re-created only by re-downloading gigabytes or re-indexing for minutes. Offered, but " +
            "never selected for you.",
        SafetyTier.UserData =>
            "Logs, histories and saved sessions living in a folder called cache. Deleting loses " +
            "them permanently.",
        SafetyTier.DoNotTouch =>
            "Config, credentials, live state, and anything Deguffer cannot prove is idle. Never " +
            "offered — a child a provider does not recognise lands here rather than being " +
            "assumed safe.",
        _ => tier.ToString(),
    };

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
