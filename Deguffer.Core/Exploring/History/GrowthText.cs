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
    /// <summary>Why a scan is not kept, in the page's words, or null where it is.</summary>
    public static string? Why(NotKept notKept) => notKept switch
    {
        NotKept.None => null,
        NotKept.NotWholeDrive => NotWholeDrive,
        NotKept.NoVolumeName => NoVolumeName,
        _ => throw new ArgumentOutOfRangeException(nameof(notKept)),
    };

    /// <summary>What the page says when a scan covered a folder rather than a whole drive.</summary>
    public const string NotWholeDrive =
        "Growth is compared between scans of a whole drive. Scan the whole drive to compare it.";

    /// <summary>What the page says when the drive has no earlier scan that can be read.</summary>
    public const string NothingEarlier =
        "No earlier scan of this drive to compare with. The next scan of it is compared with this one.";

    /// <summary>What the page says when the drive cannot be told apart from another one.</summary>
    public const string NoVolumeName =
        "Windows did not identify this drive or state its size, so its scans cannot be told from "
        + "another drive's and are not kept.";

    /// <summary>What the page says when this scan's summary could not be stored.</summary>
    public const string NotSaved =
        "This scan could not be kept, so the next one is compared with an earlier one.";

    /// <summary>What the list says when a comparison found nothing that grew or shrank.</summary>
    public const string NothingChanged = "No folder grew or shrank since then.";

    /// <summary>
    /// Why a comparison is approximate, or null where it is not: each reason that holds, because a
    /// change of route and a lower bound are wrong in different ways and §7.1 asks for both to be
    /// stated. See <see cref="GrowthApproximation"/>.
    /// </summary>
    public static string? Approximate(GrowthApproximation approximation)
    {
        var reasons = new List<string>(2);

        if (approximation.HasFlag(GrowthApproximation.RouteChanged))
        {
            reasons.Add("the two scans read the drive in different ways, and a walk of the folders counts "
                + "less than the file table, so a folder can seem to grow or shrink when only the way it "
                + "was read changed");
        }

        if (approximation.HasFlag(GrowthApproximation.LowerBound))
        {
            reasons.Add("part of the drive could not be read in one of the two scans, so some totals are "
                + "lower bounds");
        }

        return reasons.Count == 0 ? null : $"Approximate: {string.Join(". Also, ", reasons)}.";
    }

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
        FolderChangeKind.Created => $"{Signed(change.Bytes)}, created since then",
        FolderChangeKind.New => floor > 0
            ? $"{Signed(change.Bytes)}, new or under {FreeSpace.Format(floor)} then"
            : $"{Signed(change.Bytes)}, new",
        _ => Signed(change.Bytes),
    };

    /// <summary>A size with its sign, so growth and shrinkage read apart without the colour.</summary>
    public static string Signed(long bytes) =>
        bytes > 0 ? "+" + FreeSpace.Format(bytes) : bytes < 0 ? "\u2212" + FreeSpace.Format(-bytes) : "No change";
}
