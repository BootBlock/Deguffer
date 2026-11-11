namespace Deguffer.Core.Exploring.Network;

/// <summary>What a place on the network is, which decides what is listed inside it and the icon it is shown with.</summary>
public enum NetworkPlaceKind
{
    /// <summary>A computer, listed inside by the shares it offers.</summary>
    Computer,

    /// <summary>A shared folder, <c>\\server\share</c>.</summary>
    Share,

    /// <summary>A share this account has mapped to a drive letter, named by its share and shown with the letter.</summary>
    MappedDrive,

    /// <summary>A folder inside a share.</summary>
    Folder,

    /// <summary>A folder Explore scanned recently, which may be on this computer or on a share.</summary>
    Recent,
}

/// <summary>
/// One place a person can point Explore at, or open to find one.
///
/// <para><see cref="Name"/> is text from another computer, or from the preferences, and is only ever
/// shown. Nothing reads it as a path: <see cref="Path"/> is what a choice fills the box with, and the
/// page's own check still asks whether it is a folder before pointing there.</para>
/// </summary>
/// <param name="Path">The place in display form: <c>\\server</c> for a computer, <c>\\server\share\folder</c> otherwise.</param>
/// <param name="Name">What the place is called where it is listed: the computer, share or folder name alone.</param>
/// <param name="Kind">See <see cref="NetworkPlaceKind"/>.</param>
/// <param name="DriveLetter">The letter a mapped share stands behind, as <c>Z:</c>, or null.</param>
public sealed record NetworkPlace(string Path, string Name, NetworkPlaceKind Kind, string? DriveLetter = null)
{
    /// <summary>Whether the place is a computer, whose children are shares rather than folders.</summary>
    public bool IsComputer => Kind is NetworkPlaceKind.Computer;

    /// <summary>
    /// Whether the place is a folder Explore can be pointed at. A computer is not: <c>\\nas</c> names
    /// no folder, only the shares inside it do, so choosing one opens it rather than using it.
    /// </summary>
    public bool IsFolder => !IsComputer;
}
