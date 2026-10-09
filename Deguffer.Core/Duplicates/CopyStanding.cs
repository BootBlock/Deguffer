namespace Deguffer.Core.Duplicates;

/// <summary>What a page lists beside a copy (§7.4), from <see cref="CopyKeeping.Standing"/>.</summary>
/// <param name="WhyNotMarked">Why the copy may not be marked, or null where it may be.</param>
/// <param name="WhyNotKept">Why the copy cannot be the one its group keeps, where that is not why it may not be marked, or null.</param>
public sealed record CopyStanding(string? WhyNotMarked, string? WhyNotKept);
