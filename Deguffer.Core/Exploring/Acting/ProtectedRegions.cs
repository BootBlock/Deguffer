using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// The structural table <see cref="ExploreActionPolicy"/> decides from: Windows' own directories,
/// the signed-in user's profile and the folders every program keeps its state in, and Outlook's own
/// folder. Every entry says what it protects and why, because the reason is what the user is shown.
///
/// <para>Data rather than decisions, which is why it stands apart from the policy that orders and
/// reads it.</para>
/// </summary>
internal static class ProtectedRegions
{
    public static IEnumerable<ProtectedRegion> For(ISystemDirectories system, IUserEnvironment environment)
    {
        yield return ProtectedRegion.Refusing(
            system.WindowsDirectory,
            RegionScope.PathAndBelow,
            "This is inside the Windows directory. Deguffer never removes anything there from "
            + "Explore, and §9 of its specification excludes the component store and the installer "
            + "cache from every removal by path, because a wrong removal there breaks uninstall or "
            + "leaves the machine unable to roll an update back. The component store is cleaned only "
            + "by Windows' own command, from the clean list.");

        foreach (var programs in new[] { system.ProgramFiles, system.ProgramFilesX86 })
        {
            yield return ProtectedRegion.Refusing(
                programs,
                RegionScope.PathAndBelow,
                $"This is installed software, under '{programs}'. Removing part of it leaves the "
                + "program on the machine and broken, and its own uninstaller is what should take it "
                + "away.");
        }

        yield return ProtectedRegion.Refusing(
            system.ProgramData,
            RegionScope.PathAndBelow,
            "This is machine-wide application data, shared by every account on this computer. "
            + "Deguffer has classified none of it, and the caches it does know about in there are "
            + "offered on the Storage page instead, where a provider knows what they are.");

        // The user's own profile, in three entries that read as one rule. The profile directory is
        // not a thing to remove and neither is the Users folder, but everything the user keeps
        // inside their own profile is ordinary — and another account's profile is not.
        var users = Path.GetDirectoryName(environment.UserProfile);

        if (users is not null)
        {
            yield return ProtectedRegion.Refusing(
                users,
                RegionScope.PathAndBelow,
                "This belongs to another account on this computer, or is the folder holding every "
                + "account's profile. Deguffer acts only inside the profile it is signed in to.");
        }

        yield return ProtectedRegion.Permitting(environment.UserProfile, RegionScope.PathAndBelow);

        yield return ProtectedRegion.Refusing(
            environment.UserProfile,
            RegionScope.PathOnly,
            "This is your whole profile — your documents, your settings and everything Deguffer "
            + "would otherwise offer to clean. Explore removes things from inside it, never the "
            + "profile itself.");

        // The folders every program keeps its state in, and the temporary folder, in the profile's
        // shape: the folder refused, what is inside it ordinary. Providers name %LOCALAPPDATA% and
        // %TEMP% as paths that must survive a clean, and §7.1 refuses every such path here too.
        yield return ProtectedRegion.Refusing(
            environment.LocalAppData,
            RegionScope.PathOnly,
            "This is where every program keeps its local data for your account: caches, but also "
            + "settings, sign-ins and saved work. Explore removes things from inside it, never the "
            + "folder itself.");

        yield return ProtectedRegion.Refusing(
            environment.RoamingAppData,
            RegionScope.PathOnly,
            "This is where every program keeps the settings that roam with your account. Explore "
            + "removes things from inside it, never the folder itself.");

        if (environment.LocalLowAppData is { } localLow)
        {
            yield return ProtectedRegion.Refusing(
                localLow,
                RegionScope.PathOnly,
                "This is where programs that run with reduced rights, browsers among them, keep their "
                + "data for your account. Explore removes things from inside it, never the folder "
                + "itself.");
        }

        yield return ProtectedRegion.Refusing(
            environment.TempPath,
            RegionScope.PathOnly,
            "This is your temporary folder. Programs expect to find it and Windows does not put it "
            + "back, so Explore removes things from inside it, never the folder itself.");

        // Longer than the profile's permission, so it wins over it by the table's own ordering
        // rather than by being written as an exception.
        yield return OutlookDataFiles.Region(environment);
    }
}
