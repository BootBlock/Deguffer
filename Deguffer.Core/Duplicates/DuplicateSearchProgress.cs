using Deguffer.Core.Exploring;

namespace Deguffer.Core.Duplicates;

/// <summary>The stages of a duplicate search, in the order it reaches them (§7.4).</summary>
public enum DuplicateSearchStage
{
    /// <summary>Reading the locations' trees and identifying the files that may match.</summary>
    Finding,

    /// <summary>Reading the first and last blocks of the files that share a length, or the whole of a small one.</summary>
    FirstAndLastBlocks,

    /// <summary>Reading in full the larger files whose first and last blocks matched.</summary>
    FullContent,
}

/// <summary>How far a duplicate search has got.</summary>
/// <param name="Done">The files read so far in a content stage, or 0 while finding.</param>
/// <param name="Total">The files a content stage reads, or null while finding, where it is not yet known.</param>
/// <param name="Scan">How far the scan of the locations has got, while finding.</param>
public sealed record DuplicateSearchProgress(DuplicateSearchStage Stage, int Done, int? Total, ExploreProgress? Scan = null);
