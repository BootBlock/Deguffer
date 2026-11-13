namespace Deguffer.Core.Duplicates;

/// <summary>What the reads of one part of the files' content came to (<see cref="DuplicateSearchTimings"/>).</summary>
/// <param name="Files">The reads made, whatever became of each.</param>
/// <param name="Remembered">
/// Of those, the reads a checksum an earlier search read stood in for (<see cref="ChecksumCache"/>),
/// so the file was opened and judged and none of its bytes were read.
/// </param>
/// <param name="LeftOut">Of those, the reads that left their file out: gone, changed, not on this device, refused or failed.</param>
/// <param name="Bytes">
/// The bytes read from the disk: the part's length for each read that read it, and nothing for a read
/// remembered or left out.
/// </param>
/// <param name="Elapsed">From the first read starting to the last ending. Zero where none was made.</param>
public readonly record struct ContentReadTally(int Files, int Remembered, int LeftOut, long Bytes, TimeSpan Elapsed);

/// <summary>One lane of a content comparison, one a disk (<see cref="ReadingLanes"/>), and what it read.</summary>
/// <param name="Readers">
/// How many files the lane read at once: <see cref="ReadingLanes.SolidStateReaders"/> on a disk known
/// to be solid state, and one on any other.
/// </param>
/// <param name="Ends">The first and last blocks the lane read.</param>
/// <param name="Whole">The whole files the lane read.</param>
/// <param name="Elapsed">From the lane's first read starting to its last ending, both stages together.</param>
public sealed record ContentLaneTimes(int Readers, ContentReadTally Ends, ContentReadTally Whole, TimeSpan Elapsed);
