namespace Deguffer.Core.Duplicates;

/// <summary>
/// Files a search confirmed as a match on the criteria the user chose (§7.4). A match on content is
/// a match on the checksum: it groups, and never licenses a removal, which compares the bytes again.
/// </summary>
/// <param name="Criteria">What the files were matched on.</param>
/// <param name="Length">The length every file shares, or null where the length was not compared.</param>
/// <param name="Checksum">The checksum of every file's whole content, with its algorithm, or null where the content was not compared.</param>
/// <param name="Files">At least two different files, each with its role, storage and identity.</param>
public sealed record DuplicateGroup(
    MatchCriteria Criteria,
    long? Length,
    ContentChecksum? Checksum,
    IReadOnlyList<DuplicateCandidate> Files);
