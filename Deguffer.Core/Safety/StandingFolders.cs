namespace Deguffer.Core.Safety;

/// <summary>
/// The folders no removal may take whole or take with something that holds them: a drive or share
/// root, the folders Windows is built out of, the account's profile and its three tiers of
/// application data, and the account's own folders — Desktop, Documents, Downloads and the rest,
/// wherever Windows has moved them.
///
/// <para><b>One answer for every place that can be handed a folder.</b> A provider told where a
/// cache is by a setting asks this before the folder becomes a target, and the executor asks it again
/// for every path a step would destroy, because a setting is something anything on the machine may
/// have written. A variable naming Downloads as a cache is the case that made it one answer: each
/// provider had its own list, none of the lists held the account's own folders, and §5.6 protected
/// only the profile above the folder, so a run that took all of Downloads verified as clean.</para>
///
/// <para><b>The folder and what holds it, never what is inside it.</b> A cache kept in
/// <c>Documents\PCSX2</c> or <c>Downloads\vcpkg-cache</c> is a folder somebody chose to put there,
/// and whether it is the tool's is for the provider's own evidence to decide.</para>
///
/// <para><b>Asked at every path the folder is reachable at.</b> The locations are named the way
/// Windows names them, and a path's text matches them only when it is reached the same way. With the
/// system volume also mounted at <c>D:\SysMount\</c>, or <c>S:</c> substituted for the profile,
/// <c>S:\Downloads</c> is the account's Downloads folder and its text names nothing here.
/// <see cref="ReachedFolder"/> gives each path the folder is at, and a refusal that holds at any of
/// them holds.</para>
/// </summary>
public static class StandingFolders
{
    /// <summary>
    /// Where the account's own folders are in the profile when nothing has moved them. Refused as
    /// well as wherever Windows now says they are: a folder at the default place is still the
    /// account's, and a redirection can leave its old contents behind there.
    /// </summary>
    private static readonly string[] DefaultPersonalNames =
        ["Desktop", "Documents", "Downloads", "Music", "Pictures", "Videos", "Saved Games", "OneDrive"];

    /// <summary>
    /// Why <paramref name="path"/> may not be removed, or taken with something removed, as the end of
    /// a sentence, or null where it may be.
    /// </summary>
    /// <param name="path">A full path, in either form <see cref="LongPath"/> produces.</param>
    /// <param name="volumes">Asked every other path the folder is reachable at.</param>
    public static string? WhyNotTaken(
        string path, IUserEnvironment environment, ISystemDirectories system, IVolumeInventory volumes) =>
        WhyNotTaken(ReachedFolder.At(path, volumes), environment, system);

    /// <summary>
    /// Why <paramref name="folder"/> may not be removed, or taken with something removed, as the end of
    /// a sentence, or null where it may be.
    /// </summary>
    public static string? WhyNotTaken(ReachedFolder folder, IUserEnvironment environment, ISystemDirectories system)
    {
        // A folder a volume is mounted at is as much the top of that volume as its drive letter is.
        if (folder.IsVolumeTop)
        {
            return "it is the root of a drive, a share or a volume.";
        }

        string?[] structural =
        [
            environment.UserProfile,
            environment.RoamingAppData,
            environment.LocalAppData,
            environment.LocalLowAppData,
            system.WindowsDirectory,
            system.ProgramData,
            system.ProgramFiles,
            system.ProgramFilesX86,
        ];

        // Asked before the account's own folders, because a folder holding the profile holds all of
        // them, and naming Desktop would be true and much less use.
        var held = structural
            .Where(inside => !string.IsNullOrEmpty(inside))
            .Select(inside => ReachedFolder.Comparable(inside!))
            .ToList();

        if (folder.Places.Any(place => held.Exists(inside => LongPath.Contains(place, inside))))
        {
            return "it is or holds your profile, the folders every program keeps its data in, or a folder Windows is built out of.";
        }

        var personalFolders = PersonalFolders(environment).ToList();

        foreach (var place in folder.Places)
        {
            if (personalFolders.Find(own => LongPath.Contains(place, own)) is { } personal)
            {
                return LongPath.Contains(personal, place)
                    ? "it is one of your own folders, where you keep your files."
                    : $"it holds '{LongPath.Display(personal)}', one of your own folders, where you keep your files.";
            }
        }

        return null;
    }

    /// <summary>
    /// <see cref="WhyNotTaken(ReachedFolder, IUserEnvironment, ISystemDirectories)"/>, and the
    /// account's own folder <paramref name="folder"/> is in or is.
    ///
    /// <para>For a setting whose folder is emptied of whatever is in it rather than of what a tool
    /// recognises, where being inside one of the account's own folders is as bad as being one.</para>
    /// </summary>
    public static StandingFolderVerdict Examine(ReachedFolder folder, IUserEnvironment environment, ISystemDirectories system) =>
        new(WhyNotTaken(folder, environment, system), PersonalFolderHolding(folder, environment));

    /// <summary>The account's own folder <paramref name="folder"/> is in or is, at any path it is reachable at.</summary>
    private static string? PersonalFolderHolding(ReachedFolder folder, IUserEnvironment environment)
    {
        var personalFolders = PersonalFolders(environment).ToList();

        return folder.Places
            .Select(place => personalFolders.Find(own => LongPath.Contains(own, place)))
            .FirstOrDefault(personal => personal is not null);
    }

    /// <summary>Where Windows says each of the account's own folders is, and where each is by default.</summary>
    private static IEnumerable<string> PersonalFolders(IUserEnvironment environment) =>
        environment.PersonalFolders
            .Concat(environment.UserProfile.Length > 0
                ? DefaultPersonalNames.Select(name => Path.Combine(environment.UserProfile, name))
                : [])
            .Where(own => own.Length > 0)
            .Select(ReachedFolder.Comparable);
}
