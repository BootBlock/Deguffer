namespace Deguffer.Core.Providers;

/// <summary>
/// What a provider's location actually is, for a reader who has not met it before.
///
/// <para>§7 already requires every row to state what the next use costs, and
/// <see cref="ICleanupProvider.WhatHappensOnNextUse"/> is that sentence. It answers "what do I pay",
/// which is only useful once the reader knows what they are looking at — a row named "vcpkg build
/// caches" tells somebody who has never used vcpkg nothing at all. This answers the questions in
/// front of that one: whose files these are, who publishes the thing that wrote them, what it wanted
/// them for, and whether removing them is a normal thing to do.</para>
///
/// <para>It carries no stance of its own on whether a location may be deleted. That is
/// <see cref="Deguffer.Core.Safety.SafetyTier"/>'s, and a second field saying so would be a second
/// answer free to disagree with §3. <see cref="Recommendation"/> gives the reason behind the tier's
/// verdict for this location; the verdict itself comes from the tier.</para>
/// </summary>
public sealed record ProviderDescription
{
    /// <summary>
    /// The application or toolchain that wrote these files, named as its own users would name it,
    /// with a few words saying what it is. "npm" alone identifies nothing to a reader who does not
    /// already know npm.
    /// </summary>
    public required string Application { get; init; }

    /// <summary>
    /// Who publishes <see cref="Application"/>. The point is provenance: a user deciding whether to
    /// let a cleaner near a directory is entitled to know whose directory it is.
    /// </summary>
    public required string Publisher { get; init; }

    /// <summary>
    /// What the location is for — why the application keeps it, and what it holds. Written for
    /// somebody who does not use the toolchain, because those are the rows a user cannot judge.
    /// </summary>
    public required string Purpose { get; init; }

    /// <summary>
    /// Why the tier lands where it does for this particular location: what makes it disposable, or
    /// what makes it worth keeping. It supplies the reasoning under the headline, never the headline
    /// itself — see the class remarks.
    /// </summary>
    public required string Recommendation { get; init; }
}
