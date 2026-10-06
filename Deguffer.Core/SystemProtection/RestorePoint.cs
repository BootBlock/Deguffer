namespace Deguffer.Core.SystemProtection;

/// <summary>One restore point, as System Restore lists it.</summary>
/// <param name="SequenceNumber">
/// System Restore's own number for it, which is what removes it. Windows gives each new restore point
/// a higher number than every earlier one, so the highest is the newest. Whether a number can come
/// back after System Protection is switched off and on is not documented, so a restore point is
/// matched by the whole record, never by its number alone.
/// </param>
/// <param name="Created">When it was made, in local time.</param>
/// <param name="Description">What System Restore says it was made for, such as "Windows Update".</param>
public sealed record RestorePoint(uint SequenceNumber, DateTime Created, string Description)
{
    /// <summary>How the reader identifies it, after "the restore point": what it was made for, and when.</summary>
    public string Label =>
        $"\"{(Description.Length > 0 ? Description : "(no description)")}\" of {Created:d MMMM yyyy, HH:mm}";
}

/// <summary>What System Restore listed, or why it would not.</summary>
/// <param name="Points">Every restore point, empty where the listing was not made.</param>
/// <param name="Answer">Whether System Restore answered.</param>
/// <param name="Failure">Why it did not, written for the user, or null.</param>
public sealed record RestorePointListing(
    IReadOnlyList<RestorePoint> Points,
    ListingAnswer Answer,
    string? Failure = null)
{
    public static RestorePointListing Of(IReadOnlyList<RestorePoint> points) => new(points, ListingAnswer.Listed);

    public static RestorePointListing Refused { get; } = new([], ListingAnswer.NeedsElevation);

    public static RestorePointListing Failed(string why) => new([], ListingAnswer.Failed, why);

    /// <summary>The newest restore point, or null where there is none.</summary>
    public RestorePoint? Newest => Points.Count == 0 ? null : Points.MaxBy(p => p.SequenceNumber);
}

/// <summary>
/// How Windows answered a request to list what System Protection keeps.
///
/// <para>Three answers rather than a nullable list, for the reason <see cref="Exploring.Hidden.Statement"/>
/// gives: a refusal is fixed by scanning as administrator, and any other failure is not.</para>
/// </summary>
public enum ListingAnswer
{
    /// <summary>Windows listed them.</summary>
    Listed,

    /// <summary>Windows lists them only to a process with administrator rights.</summary>
    NeedsElevation,

    /// <summary>Windows did not list them, for a reason administrator rights would not change.</summary>
    Failed,
}

/// <summary>How System Restore answered a request to remove one restore point.</summary>
public enum RemovalAnswer
{
    /// <summary>System Restore removed it.</summary>
    Removed,

    /// <summary>
    /// System Restore says the restore point does not exist or cannot be removed
    /// (<c>ERROR_INVALID_DATA</c>). Its documentation does not tell the two apart.
    /// </summary>
    NotRemovable,

    /// <summary>System Restore refuses a process without administrator rights.</summary>
    NeedsElevation,

    /// <summary>System Restore failed in some other way, or could not be loaded.</summary>
    Failed,
}
