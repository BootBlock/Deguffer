using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One directory a provider classifies the children of, and the children of it that the provider
/// recognises.
///
/// <para>§5.2's declaration is an exact-name allow-list over the immediate children of one
/// directory, so a cache that sits two levels down cannot be one of its entries. Chromium's
/// <c>Cache\Cache_Data</c> is where that first bit, and Cargo's <c>registry\cache</c> is where it
/// bit again. Teaching a <see cref="DisposableChildSet"/> to accept relative paths would change the
/// question every provider's declaration answers from "which children may this tool delete?" into
/// "which paths, at what depth, may it reach?" — strictly harder to check by reading, and being
/// checkable by reading is what the declaration is for.</para>
///
/// <para>So the containing directory becomes a level of its own, and the rule stays what it was.
/// A container is then declared Tier 4 at its parent's level, with a reason saying plainly that
/// only the one child named inside it goes — which is the case where the generic "we did not
/// recognise that" wording would be actively false, because the directory really is left standing
/// and something inside it really is being removed.</para>
///
/// <para>Shared by two providers because what it carries is a fact rather than a shape: nested
/// children are classified one containing directory at a time. Each provider still writes its own
/// levels, so "which paths may this tool delete?" is still answered by reading one table in one
/// file.</para>
/// </summary>
/// <param name="ContainerName">
/// The directory this level's children sit in, relative to the root the provider resolves. Empty
/// for that root itself.
/// </param>
/// <param name="Children">
/// What may be deleted from that directory. Anything absent is Tier 4 by construction, which is the
/// direction §5.2 requires the unknown case to fail in.
/// </param>
public sealed record CacheLevel(string ContainerName, DisposableChildSet Children)
{
    /// <summary>Where this level sits under <paramref name="root"/>.</summary>
    public string Resolve(string root) =>
        ContainerName.Length == 0 ? root : Path.Combine(root, ContainerName);
}
