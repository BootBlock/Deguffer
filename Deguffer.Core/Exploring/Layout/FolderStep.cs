namespace Deguffer.Core.Exploring.Layout;

/// <summary>Which way the map moves from one folder of a tree to another. See <see cref="FolderSteps.Between"/>.</summary>
public enum FolderStep
{
    /// <summary>
    /// No flight: the same folder drawn the same way, and, to the map, a change it has no camera to
    /// fly, such as one on an icicle or a sunburst.
    /// </summary>
    Across,

    /// <summary>Into a folder this one holds, however deep: the camera flies into its shape.</summary>
    Into,

    /// <summary>Out to a folder that holds this one, however far up: the camera pulls back from its shape.</summary>
    OutOf,

    /// <summary>
    /// To a folder where neither holds the other: the camera pulls back to the nearest folder that
    /// holds both (<see cref="FolderSteps.Meeting"/>), then flies into the other's shape, as one move.
    /// </summary>
    Through,
}
