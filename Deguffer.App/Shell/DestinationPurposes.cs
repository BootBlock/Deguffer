namespace Deguffer.App.Shell;

/// <summary>
/// What each <see cref="Destination"/> is for, in one sentence: the question its rail item states as
/// help text and in its tooltip, and the line About gives the page in its summary of the whole
/// application. One sentence in one place, so the rail and About cannot describe a page two ways.
/// </summary>
public static class DestinationPurposes
{
    /// <summary>The sentence, in sentence case and without a full stop, as help text reads.</summary>
    public static string Of(Destination destination) => destination switch
    {
        Destination.Storage => "Clean caches and build output that are safe to remove",
        Destination.Explore => "See where the space on a drive went",
        Destination.Duplicates => "Find files that are on a drive more than once",
        Destination.Health => "See what state each drive reports it is in",
        Destination.InstalledApps => "Remove entries for programs that are gone, or uninstall one",
        Destination.Startup => "Choose what Windows starts when you sign in",
        Destination.Memory => "See where the memory is going",
        Destination.Settings => "Choose how Deguffer looks, scans and cleans",
        Destination.About => "See the version, how Deguffer scans and what the safety tiers mean",
        _ => throw new ArgumentOutOfRangeException(nameof(destination), destination, "A destination with no purpose."),
    };
}
