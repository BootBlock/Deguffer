using Deguffer.Core.Exploring;

namespace Deguffer.Core.Duplicates;

/// <summary>The stages of a duplicate search, in the order it reaches them (§7.4).</summary>
public enum DuplicateSearchStage
{
    /// <summary>Reading the locations' trees and identifying the files that may match.</summary>
    Finding,

    /// <summary>
    /// Reading the files that may match: the first and last blocks of those that share a length, or
    /// the whole of a small one, and in full the larger ones whose blocks matched, both at once.
    /// </summary>
    ComparingContent,
}

/// <summary>How far a duplicate search has got.</summary>
/// <param name="Done">The bytes read so far while comparing content, or 0 while finding.</param>
/// <param name="Total">
/// While comparing content, the bytes read so far and the most still to read: what is left if every
/// file still in a group with another goes on matching. It only falls, as files stop matching, and is
/// <paramref name="Done"/> once the search ends, so the share read only grows. Null while finding,
/// where it is not yet known.
/// </param>
/// <param name="Scan">How far the scan of the locations has got, while finding.</param>
public sealed record DuplicateSearchProgress(DuplicateSearchStage Stage, long Done, long? Total, ExploreProgress? Scan = null);
