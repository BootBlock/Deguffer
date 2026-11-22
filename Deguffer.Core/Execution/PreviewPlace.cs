namespace Deguffer.Core.Execution;

/// <summary>Where the Storage preview draws a row. See <see cref="PreviewFilter.Place"/>.</summary>
public enum PreviewPlace
{
    /// <summary>Not drawn: a filter the user left on hides it.</summary>
    Hidden,

    /// <summary>In the list, among the rows that report on what a location holds.</summary>
    Listed,

    /// <summary>
    /// In the group folded away below the list, which gathers the locations waiting for a source
    /// folder. They are one answer repeated, one row per provider, and none of them has anything to
    /// report until a folder is added.
    /// </summary>
    AwaitingSourceFolders,
}
