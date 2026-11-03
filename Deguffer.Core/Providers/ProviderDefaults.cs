using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

public static class ProviderDefaults
{
    /// <summary>
    /// §3's "Default" column for a provider: its tier's answer, narrowed by what its rules rest on.
    /// A Tier 1 provider whose layout nobody has seen is offered and not ticked. See
    /// <see cref="ProviderBasis"/>.
    ///
    /// <para>One answer for the row and for a step that appears inside it, so the two cannot drift:
    /// a researched row the user ticked last time still does not tick a folder it has never shown
    /// them.</para>
    /// </summary>
    public static bool IsPreSelectedByDefault(this ICleanupProvider provider) =>
        provider.Tier.IsPreSelectedByDefault() && provider.Basis.PermitsPreSelection();
}
