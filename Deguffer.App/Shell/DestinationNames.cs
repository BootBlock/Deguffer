namespace Deguffer.App.Shell;

/// <summary>
/// What each <see cref="Destination"/> is called, in the rail, in the page's own header and in the
/// window's title. One name in one place, so the three cannot drift apart one rename at a time.
/// </summary>
public static class DestinationNames
{
    /// <summary>The application's name, which the window's title leads with.</summary>
    public const string Application = "Deguffer";

    /// <summary>The name the page is shown under, in sentence case.</summary>
    public static string Of(Destination destination) => destination switch
    {
        Destination.Storage => "Storage",
        Destination.Explore => "Explore",
        Destination.Duplicates => "Duplicates",
        Destination.Health => "Health",
        Destination.InstalledApps => "Installed apps",
        Destination.Startup => "Startup",
        Destination.Memory => "Memory",
        Destination.Settings => "Settings",
        Destination.About => "About",
        _ => throw new ArgumentOutOfRangeException(nameof(destination), destination, "A destination with no name."),
    };

    /// <summary>
    /// The window's title while <paramref name="destination"/> is shown. Alt+Tab, the taskbar and a
    /// screen reader read the title, and with the rail collapsed to its icons they are the only
    /// words that say which page is open. The title bar the window draws keeps the bare name.
    /// </summary>
    public static string WindowTitle(Destination destination) => $"{Application} - {Of(destination)}";
}
