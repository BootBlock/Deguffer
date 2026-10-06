using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring.History;

/// <summary>
/// What the page says about a comparison, other than its date, which the shell words in the user's
/// own format.
///
/// <para>Here rather than in the shell because each sentence states a rule this namespace holds: what
/// a comparison can be compared with, and in which direction it can be wrong (§7.1's "provided the
/// picture states which it is").</para>
/// </summary>
public static class GrowthText
{
    /// <summary>What the page says when a scan covered a folder rather than a whole drive.</summary>
    public const string NotWholeDrive =
        "Growth is compared between scans of a whole drive. Scan the whole drive to compare it.";

    /// <summary>What the page says when the drive has no earlier scan that can be read.</summary>
    public const string NothingEarlier =
        "No earlier scan of this drive to compare with. The next scan of it is compared with this one.";

    /// <summary>What the page says when the drive cannot be told apart from another one.</summary>
    public const string NoVolumeName =
        "Windows did not name this drive, so its scans cannot be told from another drive's and are not kept.";

    /// <summary>
    /// Why a comparison is approximate, or null where it is not. See <see cref="GrowthApproximation"/>.
    /// </summary>
    public static string? Approximate(GrowthApproximation approximation) => approximation switch
    {
        GrowthApproximation.None => null,
        GrowthApproximation.RouteChanged =>
            "Approximate: the two scans read the drive in different ways. A walk of the folders counts "
            + "less than the file table, so a folder can seem to grow or shrink when only the way it "
            + "was read changed.",
        GrowthApproximation.LowerBound =>
            "Approximate: part of the drive could not be read in one of the two scans, so some totals "
            + "are lower bounds.",
        _ => throw new ArgumentOutOfRangeException(nameof(approximation)),
    };

    /// <summary>
    /// How much <paramref name="change"/> grew or shrank, as a signed size, with what it cannot say.
    /// </summary>
    /// <param name="floor">
    /// The earlier summary's <see cref="ScanSummary.UnrecordedAtMost"/>, which bounds what a new folder
    /// held then.
    /// </param>
    public static string Change(FolderChange change, long floor) => change.Kind switch
    {
        FolderChangeKind.Removed => $"Removed, held {FreeSpace.Format(change.Before)}",
        FolderChangeKind.New => floor > 0
            ? $"{Signed(change.Bytes)}, new or under {FreeSpace.Format(floor)} then"
            : $"{Signed(change.Bytes)}, new",
        _ => Signed(change.Bytes),
    };

    /// <summary>A size with its sign, so growth and shrinkage read apart without the colour.</summary>
    public static string Signed(long bytes) =>
        bytes > 0 ? "+" + FreeSpace.Format(bytes) : bytes < 0 ? "\u2212" + FreeSpace.Format(-bytes) : "No change";
}
