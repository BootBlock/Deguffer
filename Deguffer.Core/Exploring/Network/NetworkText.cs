namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// What the Path… box and the network browser say about a place: its kind, where it came from, and
/// why it lists nothing. Here rather than in the shell for the reason <see cref="TypedFolder"/>'s
/// sentences are: a test reads the same words the reader does.
/// </summary>
public static class NetworkText
{
    /// <summary>What a place is, as a screen reader states it before the path.</summary>
    public static string Kind(NetworkPlaceKind kind) =>
        kind switch
        {
            NetworkPlaceKind.Computer => "Computer",
            NetworkPlaceKind.Share => "Share",
            NetworkPlaceKind.MappedDrive => "Mapped drive",
            NetworkPlaceKind.Folder => "Folder",
            NetworkPlaceKind.Recent => "Recent folder",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    /// <summary>Where a suggestion came from, shown under its path.</summary>
    public static string Group(NetworkGroup group) =>
        group switch
        {
            NetworkGroup.MappedDrive => "Mapped drive",
            NetworkGroup.Connected => "Connected",
            NetworkGroup.Remembered => "Remembered connection",
            NetworkGroup.Recent => "Scanned recently",
            NetworkGroup.Network => "On the network",
            NetworkGroup.Added => "Added by you",
            NetworkGroup.Listed => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, null),
        };

    /// <summary>What a suggestion says under its path: where it came from, and the letter a mapped share stands behind.</summary>
    public static string Detail(NetworkSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        var group = Group(suggestion.Group);

        return suggestion.Place.DriveLetter is { } letter
            ? (group.Length == 0 ? letter : $"{group}, {letter}")
            : group;
    }

    /// <summary>A root's name in the tree.</summary>
    public static string Root(NetworkRoot root) =>
        root switch
        {
            NetworkRoot.MappedDrives => "Mapped drives",
            NetworkRoot.ConnectedShares => "Connected and remembered shares",
            NetworkRoot.Recent => "Recent",
            NetworkRoot.Network => "Network",
            NetworkRoot.Added => "Computers you added",
            _ => throw new ArgumentOutOfRangeException(nameof(root), root, null),
        };

    /// <summary>
    /// What a node or a suggestion's place is called in the tree: its name, with the letter a mapped
    /// share stands behind.
    /// </summary>
    public static string Name(NetworkPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);

        return place.DriveLetter is { } letter ? $"{letter} ({place.Path})" : place.Name;
    }

    /// <summary>What a screen reader states for a place: its kind, then its path.</summary>
    public static string Spoken(NetworkPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);

        return place.DriveLetter is { } letter
            ? $"{Kind(place.Kind)} {letter}, {place.Path}"
            : $"{Kind(place.Kind)}, {place.Path}";
    }

    /// <summary>The line a node shows where it could not be listed.</summary>
    public static string Failure(NetworkFailure failure) =>
        failure switch
        {
            NetworkFailure.NeedsSignIn => "This needs a user name and password.",
            NetworkFailure.NotFound => "Not found. Check the name, and that the computer is switched on and connected.",
            NetworkFailure.NoAnswer => "No answer came in time. Refresh (F5) to ask again.",
            NetworkFailure.Refused => "Windows would not list this.",
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
        };

    /// <summary>What the browser says where the preferences would not save a computer added.</summary>
    public const string NotKept = "Deguffer could not save that computer to its settings, so it was not added.";

    /// <summary>What the browser says where a computer typed to add names none.</summary>
    public const string NotAComputer =
        @"That is not a computer name. Type a name or an address, such as nas, nas.local or 192.0.2.10.";
}
