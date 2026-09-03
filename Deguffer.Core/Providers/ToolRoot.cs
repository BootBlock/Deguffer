using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A directory a provider owns, and the test that says which of its children are disposable.
///
/// <para>§5.2 is enforced inside a provider by <see cref="DisposableChildSet"/>, which the provider
/// consults while it builds its own plan. That is enough while the only route to a deletion is a
/// plan. §7.1 opens a second route: Explore draws every directory on the drive and lets the user
/// pick one out of the picture, and §5.2 is not scoped to a page — <c>gradle.properties</c> beside
/// <c>.gradle\caches</c> is Tier 4 there exactly as it is here. So the rule has to be readable from
/// outside the provider, and this is the shape it is read in.</para>
///
/// <para>It carries a predicate rather than a list of names because the providers do not all
/// classify by name. Playwright's children are versioned, so it matches a browser name and a
/// numeric revision instead, and a declaration that could only hold names would have had to leave
/// that provider out — which is the one direction §5.2 must never fail in.</para>
/// </summary>
/// <param name="Path">
/// The root itself, in display form. Never a target: a provider removes only what it recognises
/// inside, and Explore refuses the root for the same reason.
/// </param>
/// <param name="Reason">
/// Why the root must survive, written for the user. Explore states it when it refuses, because
/// §7.1 requires a refusal to say what it is rather than to grey a menu item out.
/// </param>
/// <param name="Recognises">
/// Whether a child of <paramref name="Path"/>, given by name, is one this provider recognises as
/// disposable. Anything else is Tier 4 by construction.
/// </param>
public sealed record ToolRoot(string Path, string Reason, Predicate<string> Recognises)
{
    /// <summary>The usual case: the provider already holds its rule as a child set.</summary>
    public static ToolRoot Of(string path, string reason, DisposableChildSet children)
    {
        ArgumentNullException.ThrowIfNull(children);

        return new ToolRoot(path, reason, children.IsDisposable);
    }
}
