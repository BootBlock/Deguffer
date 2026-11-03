namespace Deguffer.Core.Providers;

/// <summary>
/// What a provider's rules rest on: an installation somebody has looked at, or what the vendor and
/// the tool's users say one looks like.
///
/// <para><b>It exists because some caches are worth reclaiming before anyone here has the tool.</b>
/// A provider names folders, the files that mark which build is current and the processes that hold
/// them open, and every one of those names is a claim about somebody else's software. Where the claim
/// was checked against a real installation, a wrong name was caught then. Where it was assembled
/// from documentation and reports, the only check left is the user's, so the user is told.</para>
///
/// <para>It never widens what a provider may do. A researched provider keeps every rule an observed
/// one keeps: recognised children only (§5.2), the negative verified (§5.6), and anything it does
/// not recognise left alone. What changes is that nothing it offers is ticked for the user, whatever
/// its tier, and the row says why.</para>
/// </summary>
public enum ProviderBasis
{
    /// <summary>
    /// The folders, files and processes this provider acts on were seen on at least one real
    /// installation of the tool.
    /// </summary>
    Observed,

    /// <summary>
    /// Built from the vendor's documentation and reports from the tool's users, with no installation
    /// seen. Offered, never pre-selected.
    /// </summary>
    Researched,
}

public static class ProviderBasisExtensions
{
    /// <summary>
    /// Whether a row may start ticked on this basis, before its tier has its say. Only an observed
    /// layout may: a pre-selected row is Deguffer vouching for what is in it, and a researched one
    /// has nothing to vouch with.
    /// </summary>
    public static bool PermitsPreSelection(this ProviderBasis basis) => basis == ProviderBasis.Observed;

    /// <summary>The word on a researched row's chip.</summary>
    public static string ToDisplayName(this ProviderBasis basis) => basis switch
    {
        ProviderBasis.Observed => "Observed",
        ProviderBasis.Researched => "Researched",
        _ => throw new ArgumentOutOfRangeException(nameof(basis), basis, null),
    };

    /// <summary>What the basis means for somebody deciding whether to tick the row.</summary>
    public static string ToExplanation(this ProviderBasis basis) => basis switch
    {
        ProviderBasis.Observed =>
            "Deguffer's rules for this location were checked against a real installation of the "
            + "application.",
        ProviderBasis.Researched =>
            "Deguffer's rules for this location come from the publisher's documentation and from "
            + "reports by people who use the application, not from an installation Deguffer's "
            + "developers have seen. It removes only folders it recognises and leaves everything "
            + "else alone, but it never ticks this row for you: look at what it lists before you "
            + "choose it.",
        _ => throw new ArgumentOutOfRangeException(nameof(basis), basis, null),
    };
}
