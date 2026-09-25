using System.Globalization;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether a directory name is a Steam application id: the name Steam gives each game's folder in the
/// per-game containers it keeps.
///
/// <para><b>Its own type, because §5.2 hangs on it wherever Steam files things by game.</b>
/// <c>steamapps\shadercache</c> and <c>appcache\librarycache</c> are the containers that use it,
/// through <see cref="SteamShaderCacheProvider"/> and <see cref="SteamLibraryArtworkProvider"/>. A
/// child that passes is a folder Steam made for one game; a child that fails is
/// something nobody established, and is Tier 4.</para>
///
/// <para>Valve's ids are unsigned 32-bit integers written in decimal, so the name has to parse as one
/// with no sign, space, separator or other decoration. <c>440</c> passes. <c>440.old</c>,
/// <c>-440</c>, <c>４４０</c> in full-width digits and a number past <see cref="uint.MaxValue"/> do
/// not, because Steam writes none of them.</para>
/// </summary>
public static class SteamAppId
{
    /// <summary>Whether <paramref name="name"/> is exactly a Steam application id.</summary>
    public static bool IsAppId(string name) =>
        uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _);
}
