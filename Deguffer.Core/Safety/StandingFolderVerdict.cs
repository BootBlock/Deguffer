namespace Deguffer.Core.Safety;

/// <summary>
/// What <see cref="StandingFolders.Examine"/> found about one folder.
/// </summary>
/// <param name="WhyNotTaken">
/// Why the folder may not be removed or taken with something removed, as the end of a sentence, or
/// null where it may be.
/// </param>
/// <param name="PersonalFolder">The account's own folder it is in or is, or null where it is in none.</param>
public readonly record struct StandingFolderVerdict(string? WhyNotTaken, string? PersonalFolder);
