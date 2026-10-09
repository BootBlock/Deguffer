namespace Deguffer.Core.Duplicates;

/// <summary>
/// What two files must share to be a match (§7.4). The user chooses at least one, and may combine
/// them: two videos of exactly the same size are not the same video, and the name and the size
/// together say more than either.
/// </summary>
[Flags]
public enum MatchCriteria
{
    None = 0,

    /// <summary>The file's name, compared ordinally and without regard to case.</summary>
    Name = 1,

    /// <summary>The file's length.</summary>
    Size = 2,

    /// <summary>The last-modified time, to the file system's full precision, never rounded.</summary>
    Modified = 4,

    /// <summary>The bytes, by checksum, and before any removal by reading both files. Implies the size.</summary>
    Content = 8,
}

/// <summary>What each <see cref="MatchCriteria"/> combination means for the stages of a search.</summary>
public static class MatchCriteriaRules
{
    /// <summary>Every criterion there is, for telling a combination from a value no member names.</summary>
    public const MatchCriteria All = MatchCriteria.Name | MatchCriteria.Size | MatchCriteria.Modified | MatchCriteria.Content;

    /// <summary>
    /// Whether candidates are grouped by their length. A match on content is a match on length, so a
    /// content search groups by length first whether or not the size was chosen.
    /// </summary>
    public static bool GroupsBySize(this MatchCriteria criteria) =>
        (criteria & (MatchCriteria.Size | MatchCriteria.Content)) != 0;

    public static bool GroupsByName(this MatchCriteria criteria) => criteria.HasFlag(MatchCriteria.Name);

    public static bool GroupsByTime(this MatchCriteria criteria) => criteria.HasFlag(MatchCriteria.Modified);

    /// <summary>
    /// Whether the bytes are compared, which is what a file not on this device must never be read
    /// for. A cloud file that is online-only can be matched by its name, size or time, and never by
    /// its content.
    /// </summary>
    public static bool ReadsContent(this MatchCriteria criteria) => criteria.HasFlag(MatchCriteria.Content);
}
