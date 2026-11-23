namespace Deguffer.App.Shell;

/// <summary>
/// A page the navigation rail opens. The window routes each rail item to one of these and names the
/// page from it, and each page's header names the one it is, so the rail, the header and the window
/// title read the same name (<see cref="DestinationNames"/>).
/// </summary>
public enum Destination
{
    Storage,
    Explore,
    Duplicates,
    Health,
    InstalledApps,
    Startup,
    Memory,
    Settings,
    About,
}
