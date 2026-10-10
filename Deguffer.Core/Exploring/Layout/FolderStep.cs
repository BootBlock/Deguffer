namespace Deguffer.Core.Exploring.Layout;

/// <summary>Which way the map moves from one folder of a tree to another. See <see cref="FolderSteps.Between"/>.</summary>
public enum FolderStep
{
    /// <summary>Neither holds the other: the map shows the other folder in place of this one.</summary>
    Across,

    /// <summary>Into a folder this one holds, however deep: the camera flies into its shape.</summary>
    Into,

    /// <summary>Out to a folder that holds this one, however far up: the camera pulls back from its shape.</summary>
    OutOf,
}
