namespace Deguffer.Core.ModManagers;

/// <summary>
/// What Deguffer says about a mod manager's download folder, wherever it says it: the Storage page's
/// report, Explore's description of the folder, and Explore's refusal of it. One text, so the three
/// cannot disagree.
///
/// <para><b>§5.1, in order.</b> Mod Organizer 2's own <i>Delete Installed Downloads...</i> and
/// <i>Delete Uninstalled Downloads...</i>, from its download list's menu, which move each archive and its
/// record to the Recycle Bin (<c>downloadlistview.cpp</c>, <c>DownloadManager::removeFile</c>), then
/// Vortex's <i>Delete</i> on its Downloads page, which works on a selection and deletes for good
/// (<c>DownloadView.tsx</c>, <c>IPCDownloadAdapter.ts</c>).</para>
/// </summary>
internal static class ModManagerRoutes
{
    public const string ModOrganizerRoute =
        "To remove Mod Organizer 2's downloads, right-click its Downloads list and use Delete Installed "
        + "Downloads... or Delete Uninstalled Downloads... from the menu. Mod Organizer 2 moves each archive and its "
        + "record to the Recycle Bin.";

    public const string VortexRoute =
        "To remove Vortex's downloads, select them on its Downloads page and use Delete. Vortex deletes them for "
        + "good, not to the Recycle Bin.";

    public const string WhyNothingIsOffered =
        "Deguffer only reports these folders. A mod's archive may not be available to download again: Nexus "
        + "Mods can mark a mod's older files as archived, which cannot be downloaded directly, and a file its "
        + "staff remove is gone. Reinstalling a mod and installing a collection also use these archives, and "
        + "Deguffer cannot tell which are still available.";

    public const string VortexMovedFolder =
        "Vortex can be set to download into another folder, and keeps that setting in its own database, which "
        + "Deguffer does not read. A Vortex download folder moved from its default was not looked for.";

    public static string PortableModOrganizer(string handlerRecord) =>
        $"A portable Mod Organizer 2 is found through the download handler record '{handlerRecord}', so one that "
        + "has never been set to handle Nexus Mods links was not looked for.";

    public static string VortexSummary(VortexDownloadFolder folder) =>
        $"{Capitalised(folder.Whose)}: the mod archives Vortex downloaded, in one folder per game. Vortex installs "
        + "mods from them, and uses them again to reinstall a mod or to install a collection.";

    public const string VortexRemoval =
        "Some may not download again, so remove them on Vortex's Downloads page with Delete, which deletes them "
        + "for good.";

    public static string ModOrganizerSummary(ModOrganizerInstance instance) =>
        $"The downloads of {instance.Whose}: the mod archives it downloaded, each with a .meta record of whether "
        + "it is installed. Mod Organizer 2 installs mods from them, and uses them again to reinstall one.";

    public const string ModOrganizerRemoval =
        "Some may not download again, so remove them in Mod Organizer 2 with Delete Installed Downloads... or "
        + "Delete Uninstalled Downloads..., which move them to the Recycle Bin.";

    /// <summary>Why Explore refuses one of Vortex's download folders and everything in it.</summary>
    public static string VortexRefusal(string whose) =>
        $"{Capitalised(whose)}: mod archives Vortex installs and reinstalls from, some of which may not "
        + "download again. Deguffer only reports them. Remove them on Vortex's Downloads page.";

    /// <summary>Why Explore refuses an instance's download folder and everything in it.</summary>
    public static string ModOrganizerRefusal(ModOrganizerInstance instance) =>
        $"The downloads of {instance.Whose}: mod archives it installs and reinstalls from, some of which may not "
        + "download again. Deguffer only reports them. Remove them from Mod Organizer 2's Downloads list.";

    public static string Capitalised(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
