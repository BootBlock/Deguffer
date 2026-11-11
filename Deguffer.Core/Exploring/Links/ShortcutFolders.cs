using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Links;

/// <summary>
/// The folders Windows keeps shortcuts in for people to start programs from: the Start menu, the
/// desktop and the Startup folder, this account's and the ones every account shares. A program's
/// installer puts its shortcuts here, and its uninstaller does not always take them away.
/// </summary>
public static class ShortcutFolders
{
    /// <summary>
    /// Every one of the folders Windows names, each once. A folder inside another is left out,
    /// because reading the outer one reads it: the Startup folders are inside the Start menu folders
    /// unless they have been moved.
    /// </summary>
    public static IReadOnlyList<string> Of(IUserEnvironment user, ISystemDirectories machine)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(machine);

        string[] named =
        [
            .. new[]
            {
                user.StartMenuFolder,
                machine.CommonStartMenuFolder,
                user.DesktopFolder,
                machine.CommonDesktopFolder,
                user.StartupFolder,
                machine.CommonStartupFolder,
            }
            .OfType<string>()
            .Select(LongPath.Display)
            .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        return [.. named.Where(folder => !named.Any(other => !ReferenceEquals(other, folder) && LongPath.Contains(other, folder)))];
    }
}
