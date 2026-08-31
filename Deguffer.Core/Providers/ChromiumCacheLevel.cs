using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One directory inside a Chromium profile, and the children of it that
/// <see cref="ChromiumCacheProvider"/> recognises.
///
/// <para>Two of Chromium's six cache names are grandchildren rather than children:
/// <c>Cache\Cache_Data</c> and <c>Service Worker\CacheStorage</c>. A
/// <see cref="DisposableChildSet"/> classifies a flat name against one parent, so a nested path
/// cannot be one of its entries — and teaching it to accept relative paths would change the
/// question every provider's §5.2 declaration answers from "which children may this tool delete?"
/// into "which paths, at what depth, may it reach?". That is a strictly harder question to check by
/// reading, and being able to check it by reading is what the declaration is for.</para>
///
/// <para>So the containing directory becomes a level of its own instead, and the rule stays what it
/// was: an exact-name allow-list over the immediate children of one directory. <c>Cache</c> and
/// <c>Service Worker</c> are containers rather than targets, because each holds state beside the
/// cache — <c>Cache</c> keeps Chromium's own index next to <c>Cache_Data</c>, and
/// <c>Service Worker</c> keeps registrations and <c>ScriptCache</c> next to <c>CacheStorage</c>.
/// Each is therefore declared Tier 4 at the profile level, with the reason saying plainly that only
/// the one child inside it goes. Playwright met the same shape and answered it with a stricter test
/// rather than a looser declaration; this is the same answer in a different form.</para>
/// </summary>
/// <param name="ContainerName">
/// The directory this level's children sit in, relative to the profile. Empty for the profile
/// directory itself, which is where four of the six caches live.
/// </param>
/// <param name="Children">
/// What may be deleted from that directory. Anything absent is Tier 4 by construction, which is the
/// direction §5.2 requires the unknown case to fail in.
/// </param>
public sealed record ChromiumCacheLevel(string ContainerName, DisposableChildSet Children)
{
    /// <summary>Where this level sits under <paramref name="profile"/>.</summary>
    public string Resolve(string profile) =>
        ContainerName.Length == 0 ? profile : Path.Combine(profile, ContainerName);
}
