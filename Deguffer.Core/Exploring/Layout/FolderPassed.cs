namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// The folder a change of folder between folders where neither holds the other passes through, and the
/// zoom the folder asked for is shown at once the flight has passed it. See <see cref="FolderFlight"/>.
/// </summary>
public sealed record FolderPassed(int Folder, MapViewport Opened);
