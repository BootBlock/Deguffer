namespace Deguffer.Core.Exploring.Files;

/// <summary>What the Files layout lists from below the folder on screen.</summary>
public enum FileListing
{
    /// <summary>The largest files, by size. See <see cref="LargestFiles"/>.</summary>
    LargestFiles,

    /// <summary>
    /// The shortcuts, symbolic links and junctions whose target is gone, by path. See
    /// <see cref="Links.BrokenLinks"/>.
    /// </summary>
    BrokenLinks,
}
