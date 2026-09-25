using Deguffer.Core.Execution;
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
/// <param name="UnreachedRoot">
/// The directory Steam's own record points at, where Windows would not describe it. Nothing
/// established whether it is there or holds the program, so it is neither an install nor an
/// <paramref name="UnmarkedRoot"/>, and reading it as no record at all would tell the user Deguffer
/// could not work out where Steam is when Steam's record said so plainly.
/// </param>
public sealed record SteamInstall(string? Root, string? UnmarkedRoot = null, string? UnreachedRoot = null);

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
    private SteamLibraries? _libraries;

    /// <summary>Steam's folder in the profile. Known outright, and independent of the install.</summary>
    public string LocalRoot { get; } =
        Path.Combine(environment.LocalAppData, LocalDirectoryName);

    /// <summary>
    /// The install directory, memoised for the life of a planning pass (G4). Presence, planning and
    /// the §5.2 declarations all ask the same question of the same registry value.
    /// </summary>
    public SteamInstall Install => _install ??= Find();

    /// <summary>
    /// Every game library the install's own list names, memoised with <see cref="Install"/>. Null
    /// where the install itself was not found, because the list is kept inside it and there is then
    /// nothing to read it from.
    /// </summary>
    public SteamLibraries? Libraries =>
        Install.Root is { } root ? _libraries ??= SteamLibraryFolders.Of(root) : null;

    /// <summary>
    /// Drop the memoised answers, so a Steam installed, or a library added, while the app was open is
    /// seen.
    /// </summary>
    public void Invalidate()
    {
        _install = null;
        _libraries = null;
    }

    /// <summary>
    /// The sentence a plan owes about an install directory this machine gave no usable answer for, or
    /// null when it was found — or when there is no Steam in this profile to be missing one.
    ///
    /// <para>Gated on the profile folder because the alternative is to tell somebody who has never
    /// installed Steam that Deguffer could not find it. A profile folder Windows would not describe
    /// passes the gate: it is no evidence that Steam was never installed, and the sentence it lets
    /// through is true either way.</para>
    ///
    /// <para>A warning where Windows would not describe the recorded directory, because that is not
    /// Deguffer's own decision, and information otherwise. Here rather than on a provider because
    /// every provider that looks inside the install owes the same sentence, and only what it did not
    /// look at differs.</para>
    /// </summary>
    /// <param name="unexamined">
    /// What was therefore not looked at, as a singular noun phrase in lower case, such as "the cache
    /// Steam keeps beside the program".
    /// </param>
    public PlanNote? UnreachedInstallNote(string unexamined)
    {
        if (Install.Root is not null)
        {
            return null;
        }

        var sentenceCase = char.ToUpperInvariant(unexamined[0]) + unexamined[1..];

        if (Install.UnreachedRoot is { } refused)
        {
            return new PlanNote(
                PlanNoteSeverity.Warning,
                $"Windows records Steam as installed in '{refused}', and would not say what is there, so "
                + "Deguffer could not check for the Steam program or look inside it. A link Windows will "
                + "not follow, a folder this account may not read and a drive that is not connected all do "
                + $"that. {sentenceCase} was neither cleared nor ruled out.");
        }

        if (!LongPath.DirectoryMayExist(LocalRoot))
        {
            return null;
        }

        return new PlanNote(
            PlanNoteSeverity.Information,
            Install.UnmarkedRoot is { } unmarked
                ? $"Windows records Steam as installed in '{unmarked}', but the Steam program is not "
                    + $"there. Deguffer did not look inside it, so {unexamined} was neither cleared nor "
                    + "ruled out."
                : "Deguffer could not work out where Steam is installed, so it did not look at "
                    + $"{unexamined}. It was neither cleared nor ruled out.");
    }

    private SteamInstall Find()
    {
        var recorded = environment.ReadCurrentUserRegistryValue(RegistryKey, InstallPathValue);

        // Steam writes this as "c:/program files (x86)/steam", so it needs normalising before it can
        // be compared with or joined to anything. Configured also refuses a relative value, which
        // would otherwise resolve against Deguffer's own working directory.
        if (LongPath.Configured(recorded) is not { } root)
        {
            return new SteamInstall(null);
        }

        switch (LongPath.ProbeDirectory(root))
        {
            // A recorded directory that is no longer there is the same answer as no record at all:
            // there is nothing at that path to examine, so there is nothing to tell the user about
            // it beyond the sentence a null already produces.
            case PathPresence.Absent:
                return new SteamInstall(null);

            // Asked before the marker, whose two-state answer would read a refusal as "not Steam".
            case PathPresence.Refused:
                return new SteamInstall(null, UnreachedRoot: root);
        }

        return LongPath.FileExists(Path.Combine(root, RootMarker))
            ? new SteamInstall(root)
            : new SteamInstall(null, root);
    }
}
