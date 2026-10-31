namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Which way the map's camera flies to a new picture, and, for a step through a folder, the folder
/// passed through. See <see cref="PictureChange.Step"/>.
/// </summary>
/// <param name="Way">Which way the camera flies, or <see cref="FolderStep.Across"/> for no flight.</param>
/// <param name="Meeting">
/// The nearest folder that holds both ends (<see cref="FolderSteps.Meeting"/>) where
/// <paramref name="Way"/> is <see cref="FolderStep.Through"/>, and null otherwise.
/// </param>
public readonly record struct PictureStep(FolderStep Way, int? Meeting)
{
    /// <summary>No flight: the new picture replaces the one on screen where it is.</summary>
    public static PictureStep Across { get; } = new(FolderStep.Across, null);
}
