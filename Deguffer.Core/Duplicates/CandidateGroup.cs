using Deguffer.Core.Scanning;

namespace Deguffer.Core.Duplicates;

/// <summary>A file a search found that may have a match, as the scan saw it.</summary>
/// <param name="Path">Where the scan found it, in display form.</param>
/// <param name="Length">Its length in bytes, which is what a size match compares.</param>
/// <param name="Storage">
/// How it is stored as the scan saw it. A cloud file that was online-only then is never in a content
/// search, and the check before any read asks again.
/// </param>
/// <param name="Role">The role of the innermost location holding it.</param>
public sealed record DuplicateCandidate(string Path, string Name, long Length, FileStorage Storage, LocationRole Role);

/// <summary>
/// Files that share what a search compares before it reads anything: the length where the size or
/// the content is a criterion, and the name where the name is. Each group holds at least two files.
/// </summary>
/// <param name="Length">The length every file shares, or null where the length was not compared.</param>
/// <param name="Name">The name every file shares, ignoring case, or null where the name was not compared.</param>
public sealed record CandidateGroup(long? Length, string? Name, IReadOnlyList<DuplicateCandidate> Files);

/// <summary>
/// What a search left out as it read the tree, counted so that a search which found nothing in a
/// place is never read as one that found nothing there because it did not look.
/// </summary>
/// <param name="Links">Symbolic links, junctions and mount points: never followed, and never a file to match.</param>
/// <param name="Empty">Empty files, which are never matched: an empty file's name is its content.</param>
/// <param name="UnknownLength">Files whose length the scan could not establish.</param>
/// <param name="OnlyInTheCloud">
/// Cloud files not on this device, left out of a search that compares content because reading one
/// would download it.
/// </param>
public readonly record struct LeftOutFiles(int Links, int Empty, int UnknownLength, int OnlyInTheCloud);
