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
    public static string? WhyNotTaken(string path, IUserEnvironment environment, ISystemDirectories system)
    {
        var folder = Comparable(path);

        if (string.IsNullOrEmpty(Path.GetDirectoryName(folder)))
        {
            return "it is the root of a drive or a share.";
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
        if (structural.Any(inside => !string.IsNullOrEmpty(inside) && LongPath.Contains(folder, Comparable(inside))))
        {
            return "it is or holds your profile, the folders every program keeps its data in, or a folder Windows is built out of.";
        }

        if (PersonalFolders(environment).FirstOrDefault(own => LongPath.Contains(folder, own)) is not { } personal)
        {
            return null;
        }

        return LongPath.Contains(personal, folder)
            ? "it is one of your own folders, where you keep your files."
            : $"it holds '{LongPath.Display(personal)}', one of your own folders, where you keep your files.";
    }

    /// <summary>
    /// The account's own folder <paramref name="path"/> is in, or is, or null where it is in none.
    /// For a setting whose folder is emptied of whatever is in it rather than of what a tool
    /// recognises, where being inside one of these is as bad as being one.
    /// </summary>
    public static string? PersonalFolderHolding(string path, IUserEnvironment environment)
    {
        var folder = Comparable(path);

        return PersonalFolders(environment).FirstOrDefault(own => LongPath.Contains(own, folder));
    }

    /// <summary>Where Windows says each of the account's own folders is, and where each is by default.</summary>
    private static IEnumerable<string> PersonalFolders(IUserEnvironment environment) =>
        environment.PersonalFolders
            .Concat(environment.UserProfile.Length > 0
                ? DefaultPersonalNames.Select(name => Path.Combine(environment.UserProfile, name))
                : [])
            .Where(own => own.Length > 0)
            .Select(Comparable);

    /// <summary>
    /// Without an 8.3 alias, the extended-length prefix or a trailing separator, so a path from a step
    /// and one from the environment compare as the same folder. A root keeps its separator.
    /// </summary>
    private static string Comparable(string path) =>
        Path.TrimEndingDirectorySeparator(LongPath.Display(LongPath.Unaliased(path)));
}
