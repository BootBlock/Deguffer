using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Duplicates;

/// <summary>A folder a program is installed in, which a search passes over by default.</summary>
/// <param name="Reached">Every place the folder is reachable at as its entry names it.</param>
/// <param name="Program">The program's name as its entry gives it, which is what the page says.</param>
/// <param name="Final">
/// The folder the entry's path leads to once every link on the way is followed, or null where Windows
/// would not open it (<see cref="ResolvedPlaces.FollowedTo"/>). A search's paths are final paths, so a
/// program installed at <c>D:\Apps\Tool</c>, where <c>D:\Apps</c> is a junction to <c>E:\Apps</c>, is
/// at <c>E:\Apps\Tool</c> to the search.
/// </param>
public sealed record ProgramFolder(string Path, ReachedFolder Reached, string Program, ReachedFolder? Final)
{
    /// <summary>The folder as its entry names it, and where that leads.</summary>
    internal IEnumerable<ReachedFolder> Forms => Final is null ? [Reached] : [Reached, Final];

    /// <summary>Whether <paramref name="path"/> is in the folder, at any place it is reachable at.</summary>
    /// <param name="path">A full path, in either form <see cref="LongPath"/> produces.</param>
    public bool Holds(string path) => Forms.Any(form => form.PathTo(path) is not null);

    /// <summary>Whether <paramref name="other"/> is the same folder, at any place either is reachable at.</summary>
    internal bool IsSameAs(ProgramFolder other) => Forms.Any(form => other.Forms.Any(form.IsSameAs));
}

/// <summary>An installed program's entry whose install location names nothing a search can pass over.</summary>
/// <param name="Reason">Why, as the end of a sentence that starts with the location.</param>
public sealed record SetAsideInstallLocation(string Program, string Location, string Reason);

/// <summary>What <see cref="ProgramFolders.Read"/> found.</summary>
/// <param name="Folders">The program folders a search passes over by default.</param>
/// <param name="Installed">
/// Every folder a program is installed in, which a copy is refused in (§7.4): <paramref name="Folders"/>,
/// and each install location set aside only because it holds a location the search was asked to
/// search. Searching inside a program's folder does not make the program's files the user's to
/// remove, and a program's private library is how the established tools have broken programs.
/// </param>
/// <param name="Unread">
/// The lists of installed programs Windows would not read, whose folders are therefore not known to be
/// program folders. Reported, because a search that meant to pass over a program's folder and could
/// not must not read as one that did.
/// </param>
public sealed record ProgramFolderReading(
    IReadOnlyList<ProgramFolder> Folders,
    IReadOnlyList<ProgramFolder> Installed,
    IReadOnlyList<SetAsideInstallLocation> SetAside,
    IReadOnlyList<UninstallScope> Unread);

/// <summary>
/// The folders installed programs live in, which a duplicate search passes over by default (§7.4):
/// two programs that ship the same library each need their own copy, and a search of them finds
/// thousands of matches nobody should act on.
///
/// <para><b>Where each comes from.</b> The folder each installed program's entry names as its
/// install location, read from the keys §7.3 reads, and <c>%LOCALAPPDATA%\Programs</c>, where
/// per-user installers put programs whether or not they name it.</para>
///
/// <para><b>An entry can name anything, so some are set aside.</b> An installer that writes a drive's
/// top, a profile, a folder Windows keeps for the user such as Documents, or a folder holding one of
/// those has named nothing a search can pass over without passing over the user's own files.
/// <see cref="StandingFolders"/> already says which folders those are, for the removals that must
/// never take one, so it is asked here rather than restated. A location holding a folder the user
/// chose to search would silence that choice, so it is set aside too, and is still a program's folder
/// that a copy is refused in. Each set-aside entry is named, so the user sees what was not passed
/// over and why.</para>
/// </summary>
public static class ProgramFolders
{
    /// <param name="chosen">The locations the search covers, which no program folder may hold.</param>
    internal static ProgramFolderReading Read(
        IUninstallRegistry registry,
        IUserEnvironment environment,
        ISystemDirectories system,
        IVolumeInventory volumes,
        FileInformation files,
        IReadOnlyList<ResolvedLocation> chosen,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(chosen);

        List<ProgramFolder> folders = [];
        List<ProgramFolder> installed = [];
        List<SetAsideInstallLocation> setAside = [];
        List<UninstallScope> unread = [];

        var profiles = Path.GetDirectoryName(environment.UserProfile) is { Length: > 0 } users
            ? ReachedFolder.At(users, volumes)
            : null;

        void Consider(string program, string location)
        {
            var folder = new ProgramFolder(
                location, ReachedFolder.At(location, volumes), program, ResolvedPlaces.FollowedTo(location, volumes, files));

            // Asked of where it leads too: an entry naming a junction to the profile has named the profile.
            if (folder.Forms.Select(form => WhyNamesNoProgram(form, environment, system, profiles)).FirstOrDefault(why => why is not null) is { } why)
            {
                setAside.Add(new SetAsideInstallLocation(program, location, why));
                return;
            }

            if (!installed.Exists(folder.IsSameAs))
            {
                installed.Add(folder);
            }

            if (chosen.FirstOrDefault(choice => folder.Forms.Any(form => form.Holds(choice.Reached))) is { } held)
            {
                setAside.Add(new SetAsideInstallLocation(program, location, $"it holds '{held.Folder}', which this search was asked to search."));
            }
            else if (!folders.Exists(folder.IsSameAs))
            {
                folders.Add(folder);
            }
        }

        Consider("Programs installed for your account alone", Path.Combine(environment.LocalAppData, "Programs"));

        foreach (var scope in UninstallScopes.All)
        {
            ct.ThrowIfCancellationRequested();

            var read = registry.Read(scope);

            if (read.Presence is PathPresence.Refused)
            {
                unread.Add(scope);
            }

            foreach (var record in read.Records)
            {
                if (InstallLocation.Of(record.Values) is { Kind: InstallLocationKind.Path, Text: var location })
                {
                    Consider(record.Values.Text("DisplayName") ?? record.Key.Name, location);
                }
            }
        }

        return new ProgramFolderReading(folders, installed, setAside, unread);
    }

    /// <summary>
    /// Why an entry's install location names no program's folder at all, so that it is neither
    /// passed over nor a folder a copy is refused in, or null where it names one.
    /// </summary>
    private static string? WhyNamesNoProgram(
        ReachedFolder location,
        IUserEnvironment environment,
        ISystemDirectories system,
        ReachedFolder? profiles)
    {
        if (StandingFolders.WhyNotTaken(location, environment, system) is { } standing)
        {
            return standing;
        }

        // StandingFolders knows this account's profile and not another's, which a search passes over
        // anyway. An entry naming one has still named an account's whole profile, not a program.
        if (profiles is not null && profiles.LevelsTo(location) == 1)
        {
            return "it is the profile of an account on this computer.";
        }

        return null;
    }
}
