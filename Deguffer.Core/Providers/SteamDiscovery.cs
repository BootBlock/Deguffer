using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>Where Steam turned out to be on this machine.</summary>
/// <param name="Root">
/// The install directory, or null when this machine gives no usable way to find it. Null is a
/// sentence the plan has to say out loud rather than a smaller number nobody can account for: the
/// cache Steam keeps beside the program is then neither offered nor ruled out.
/// </param>
/// <param name="UnmarkedRoot">
/// A directory Steam's own record points at which does not carry the marker, and which is therefore
/// not treated as an install. Reported separately from a plain null <paramref name="Root"/> for the
/// reason <see cref="VcpkgLocations.UnmarkedRoot"/> is: "nothing said where it is" and "something
/// did and Deguffer declined it" are different facts, and the user is owed the second one.
/// </param>
public sealed record SteamInstall(string? Root, string? UnmarkedRoot = null);

/// <summary>
/// Finds Steam. Separate from the provider for the reason <see cref="VcpkgDiscovery"/> and
/// <see cref="ChromiumUserDataDiscovery"/> are: one type answers "where is this tool?" and the other
/// answers "what inside it may go", and keeping them apart is what stops the second question from
/// being asked of a directory that failed the first.
///
/// <para>Steam is split across two roots and only one of them is knowable from the profile.
/// <c>%LOCALAPPDATA%\Steam</c> is where the client keeps its embedded browser's cache, and it is
/// always in the same place. The install directory holds a second cache beside the program, moves
/// with whichever drive the user gave their game library, and is recorded in exactly one place.</para>
/// </summary>
public sealed class SteamDiscovery(IUserEnvironment environment)
{
    /// <summary>Steam's own key under <c>HKEY_CURRENT_USER</c>, which the client writes as it starts.</summary>
    public const string RegistryKey = @"Software\Valve\Steam";

    /// <summary>The value holding the install directory, in Steam's own forward-slash form.</summary>
    public const string InstallPathValue = "SteamPath";

    /// <summary>Steam's own directory under <c>%LOCALAPPDATA%</c>.</summary>
    public const string LocalDirectoryName = "Steam";

    /// <summary>
    /// The client itself, required to be present before a recorded path is treated as an install.
    ///
    /// <para>This is the identification check vcpkg's <c>.vcpkg-root</c> makes, for the same reason:
    /// something pointing at a directory is not evidence of what that directory is. A stale value
    /// left behind by an uninstall, or one edited by hand, would otherwise have this provider
    /// declare <c>appcache</c> under whatever it names.</para>
    ///
    /// <para>It is also what makes a link above the install directory harmless. Everything else in
    /// this project derives a path from a known root and must therefore check every segment of it
    /// for a junction, because a redirected path resolves somewhere nothing established. Here the
    /// marker establishes identity <em>at the resolved location</em>: whatever <c>D:\Games\Steam</c>
    /// turns out to be after Windows has followed the links above it, Deguffer proceeds only if
    /// Steam's own client is sitting in it.</para>
    /// </summary>
    public const string RootMarker = "steam.exe";

    private SteamInstall? _install;

    /// <summary>Steam's folder in the profile. Known outright, and independent of the install.</summary>
    public string LocalRoot { get; } =
        Path.Combine(environment.LocalAppData, LocalDirectoryName);

    /// <summary>
    /// The install directory, memoised for the life of a planning pass (G4). Presence, planning and
    /// the §5.2 declarations all ask the same question of the same registry value.
    /// </summary>
    public SteamInstall Install => _install ??= Find();

    /// <summary>Drop the memoised answer, so a Steam installed while the app was open is seen.</summary>
    public void Invalidate() => _install = null;

    private SteamInstall Find()
    {
        var recorded = environment.ReadCurrentUserRegistryValue(RegistryKey, InstallPathValue);

        // Steam writes this as "c:/program files (x86)/steam", so it needs normalising before it can
        // be compared with or joined to anything. Configured also refuses a relative value, which
        // would otherwise resolve against Deguffer's own working directory.
        if (LongPath.Configured(recorded) is not { } root || !LongPath.DirectoryExists(root))
        {
            // A recorded directory that is no longer there is the same answer as no record at all:
            // there is nothing at that path to examine, so there is nothing to tell the user about
            // it beyond the sentence a null already produces.
            return new SteamInstall(null);
        }

        return LongPath.FileExists(Path.Combine(root, RootMarker))
            ? new SteamInstall(root)
            : new SteamInstall(null, root);
    }
}
