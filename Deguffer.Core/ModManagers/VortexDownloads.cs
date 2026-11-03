using Deguffer.Core.Safety;

namespace Deguffer.Core.ModManagers;

/// <summary>
/// One of Vortex's default download folders, and what was in it.
/// </summary>
/// <param name="Folder">The download folder.</param>
/// <param name="Shared">Whether it is the folder Vortex uses in its shared mode, for every account on the machine.</param>
/// <param name="Presence">What Windows said about the folder.</param>
/// <param name="Games">
/// One folder per game, named by Vortex's own identifier for it, such as <c>skyrimse</c>. Empty where the
/// folder is not there or would not be listed.
/// </param>
/// <param name="Listed">False where the folder is there and Windows would not list it.</param>
internal sealed record VortexDownloadFolder(
    string Folder,
    bool Shared,
    PathPresence Presence,
    IReadOnlyList<string> Games,
    bool Listed)
{
    public string Whose => VortexDownloads.Whose(Shared);
}

/// <summary>
/// Where Vortex keeps the archives it downloads where its setting is left at the default.
///
/// <para>The default is <c>{USERDATA}\downloads</c>, with a folder per game inside it
/// (<c>getDownloadPath.ts</c>, <c>selectors.ts</c>). <c>{USERDATA}</c> is <c>%APPDATA%\Vortex</c> for one
/// account, and <c>%PROGRAMDATA%\vortex</c> where Vortex runs in its shared mode (<c>Application.ts</c>).
/// The setting that moves it is kept in Vortex's own database, which is not read, so a moved folder is
/// not found and the report says so.</para>
/// </summary>
internal static class VortexDownloads
{
    public static string Default(IUserEnvironment environment) =>
        Path.Combine(environment.RoamingAppData, "Vortex", "downloads");

    public static string SharedDefault(ISystemDirectories system) =>
        Path.Combine(system.ProgramData, "vortex", "downloads");

    /// <summary>The folder as the report and Explore name it.</summary>
    public static string Whose(bool shared) => shared ? "Vortex's shared downloads" : "Vortex's downloads";

    public static IReadOnlyList<VortexDownloadFolder> Read(IUserEnvironment environment, ISystemDirectories system)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(system);

        return [Folder(Default(environment), shared: false), Folder(SharedDefault(system), shared: true)];
    }

    private static VortexDownloadFolder Folder(string folder, bool shared)
    {
        var presence = LongPath.ProbeDirectory(folder);

        if (presence != PathPresence.Present)
        {
            return new VortexDownloadFolder(folder, shared, presence, [], Listed: presence == PathPresence.Absent);
        }

        // A link in here is not a folder Vortex made for a game, so it has no line of its own.
        var children = ChildDirectories.Under(folder);

        return new VortexDownloadFolder(
            folder,
            shared,
            presence,
            [.. children.Directories.Select(game => LongPath.Display(game.FullName))],
            Listed: !children.Unreadable);
    }
}
