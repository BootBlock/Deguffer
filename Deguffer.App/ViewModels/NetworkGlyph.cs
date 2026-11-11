using Deguffer.Core.Exploring.Network;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Segoe Fluent Icons glyph that says what kind of network place a row is, so a list of paths
/// can be read by kind before it is read by name. Presentation alone: the kind is Core's.
/// </summary>
public static class NetworkGlyph
{
    private const string Computer = "";
    private const string Share = "";
    private const string MappedDrive = "";
    private const string Folder = "";
    private const string Recent = "";
    private const string Discovered = "";

    public static string Of(NetworkPlaceKind kind) =>
        kind switch
        {
            NetworkPlaceKind.Computer => Computer,
            NetworkPlaceKind.Share => Share,
            NetworkPlaceKind.MappedDrive => MappedDrive,
            NetworkPlaceKind.Folder => Folder,
            NetworkPlaceKind.Recent => Recent,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    public static string Of(NetworkRoot root) =>
        root switch
        {
            NetworkRoot.MappedDrives => MappedDrive,
            NetworkRoot.ConnectedShares => Share,
            NetworkRoot.Recent => Recent,
            NetworkRoot.Network => Discovered,
            NetworkRoot.Added => Computer,
            _ => throw new ArgumentOutOfRangeException(nameof(root), root, null),
        };
}
