using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Which of the profile's application-data tiers a declared location sits in.
///
/// A tier is named rather than a path held, because a declaration such as <see cref="ShaderCacheRoot"/>
/// or <see cref="ChromiumHost"/> stays static and the paths come from
/// <see cref="IUserEnvironment"/> at the moment they are needed.
/// </summary>
public enum ProfileArea
{
    /// <summary><c>%LOCALAPPDATA%</c>.</summary>
    LocalAppData,

    /// <summary><c>%USERPROFILE%\AppData\LocalLow</c>.</summary>
    LocalLowAppData,

    /// <summary><c>%APPDATA%</c>.</summary>
    RoamingAppData,
}

/// <summary>Where a <see cref="ProfileArea"/> is on one machine.</summary>
public static class ProfileAreaLocation
{
    /// <summary>
    /// The tier's directory on <paramref name="environment"/>'s machine, or null when the tier
    /// itself could not be located — which only LocalLow can be, and which §5.2 says is not to be
    /// guessed at.
    /// </summary>
    public static string? In(this ProfileArea area, IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return area switch
        {
            ProfileArea.LocalAppData => environment.LocalAppData,
            ProfileArea.LocalLowAppData => environment.LocalLowAppData,
            ProfileArea.RoamingAppData => environment.RoamingAppData,

            // A tier this method does not resolve yields no path, rather than falling back on the
            // one it happens to know. §5.2's direction, one level up from a child: a row whose
            // location is not established contributes nothing, where a fallback would quietly plan
            // deletions in a real directory the row never named.
            _ => null,
        };
    }
}
