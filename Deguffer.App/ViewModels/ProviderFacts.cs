using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What one Storage row says about the provider behind it: its §3 tier and the basis of its rules,
/// each with the sentence that explains it, whose files these are, and what happens on next use.
///
/// <para>Apart from <see cref="FindingViewModel"/> because none of it moves with the plan, the keep list
/// or the ticks. It is fixed by the provider, so the row makes it once and keeps it through every
/// re-plan, as it does its <see cref="FindingAutomationIds"/>.</para>
/// </summary>
public sealed class ProviderFacts(ICleanupProvider provider)
{
    public SafetyTier Tier { get; } = provider.Tier;

    public string TierLabel { get; } = provider.Tier.ToDisplayName();

    /// <summary>
    /// What the badge's tier means, for the reader who has never opened the About page. §3's
    /// classification is the product, and a two-word chip states it without explaining it.
    /// </summary>
    public string TierExplanation { get; } = provider.Tier.ToExplanation();

    /// <summary>
    /// §3's verdict on this row, worded for somebody deciding whether to tick it. Read off the tier
    /// rather than off <see cref="Core.Execution.Finding.IsPreSelectedByDefault"/>, which is the same
    /// decision narrowed by whether there is anything here to reclaim — an empty cache would otherwise
    /// be advised against on a day it happened to be empty.
    /// </summary>
    public string CleaningAdvice { get; } = provider.Tier.ToCleaningAdvice();

    /// <summary>
    /// Whether this row's rules come from research rather than an installation anybody looked at,
    /// which the row says on a chip of its own. See <see cref="ProviderBasis"/>.
    /// </summary>
    public bool IsResearched { get; } = !provider.Basis.PermitsPreSelection();

    public string BasisLabel { get; } = provider.Basis.ToDisplayName();

    public string BasisExplanation { get; } = provider.Basis.ToExplanation();

    public string WhatHappensOnNextUse { get; } = provider.WhatHappensOnNextUse;

    /// <summary>
    /// Whose files this row is about and what they are for — the answer to the question in front of
    /// the one <see cref="WhatHappensOnNextUse"/> answers. Handed through whole rather than
    /// unpacked into four properties: the dialog is the only reader, and it wants all four.
    /// </summary>
    public ProviderDescription Description { get; } = provider.Description;
}
