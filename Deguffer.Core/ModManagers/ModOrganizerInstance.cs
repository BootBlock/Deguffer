namespace Deguffer.Core.ModManagers;

/// <summary>
/// One Mod Organizer 2 instance, and the folder its settings keep downloads in.
/// </summary>
/// <param name="Name">The instance's name: its folder's name, as Mod Organizer 2 lists it.</param>
/// <param name="Folder">The folder holding the instance's <c>ModOrganizer.ini</c>.</param>
/// <param name="Portable">
/// Whether it is a portable instance, kept beside <c>ModOrganizer.exe</c>, rather than one of the
/// instances Mod Organizer 2 keeps in <c>%LOCALAPPDATA%\ModOrganizer</c>.
/// </param>
/// <param name="Named">The download folder the settings name, resolved, or null where they name none Deguffer can read.</param>
/// <param name="Downloads">The download folder to measure as this instance's, or null where it is declined.</param>
/// <param name="Declined">Why the download folder is not measured as the instance's, as the end of a sentence, or null.</param>
internal sealed record ModOrganizerInstance(
    string Name,
    string Folder,
    bool Portable,
    string? Named,
    string? Downloads,
    string? Declined)
{
    /// <summary>The instance as the report names it.</summary>
    public string Whose => Portable
        ? $"the portable Mod Organizer 2 instance in '{Folder}'"
        : $"the Mod Organizer 2 instance '{Name}'";
}
