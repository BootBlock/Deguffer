namespace Deguffer.Core.SystemProtection;

/// <summary>One shadow copy, as the Volume Shadow Copy service lists it.</summary>
/// <param name="Id">The service's identifier for it, which no other shadow copy is ever given.</param>
/// <param name="Provider">The shadow copy provider that made it.</param>
/// <param name="Attributes">Its <c>VSS_VOLUME_SNAPSHOT_ATTRIBUTES</c>.</param>
/// <param name="Volume">The volume it is a copy of, as the service names it.</param>
/// <param name="Created">When it was made, in local time.</param>
public sealed record ShadowCopy(Guid Id, Guid Provider, int Attributes, string Volume, DateTime Created)
{
    /// <summary>
    /// <c>VSS_SWPRV_ProviderId</c>, the provider that is part of Windows, and the only one that can make
    /// a shadow copy of the kind System Restore keeps.
    /// </summary>
    public static readonly Guid SystemProvider = new("b5946137-7b9f-4925-af80-51abd60b20d5");

    private const int ClientAccessible = 0x4;

    private const int NoWriters = 0x10;

    /// <summary>
    /// Whether this could be one of System Restore's own shadow copies.
    ///
    /// <para><b>A test that can rule a copy out and never rule one in.</b> Windows documents no link
    /// from a restore point to its shadow copies. What it documents is that a client-accessible copy
    /// with its writers involved (<c>VSS_CTX_CLIENT_ACCESSIBLE_WRITERS</c>) can be made only by the system
    /// provider. A copy from another provider, one that is not client-accessible, or one made without
    /// writers is therefore never a restore point's, and removing restore points must leave it. A copy
    /// that passes may still belong to something else, such as Windows' own backup, and nothing here
    /// can tell.</para>
    /// </summary>
    public bool CouldBeRestorePoint =>
        Provider == SystemProvider && (Attributes & ClientAccessible) != 0 && (Attributes & NoWriters) == 0;

    /// <summary>How the reader identifies it: the volume, when it was made, and its identifier.</summary>
    public string Named => $"The shadow copy of {Volume} made {Created:d MMMM yyyy, HH:mm} ({Id:B})";
}

/// <summary>What the Volume Shadow Copy service listed, or why it would not.</summary>
/// <param name="Copies">Every shadow copy on the machine, empty where the listing was not made.</param>
/// <param name="Answer">Whether the service answered.</param>
/// <param name="Failure">Why it did not, written for the user, or null.</param>
public sealed record ShadowCopyListing(
    IReadOnlyList<ShadowCopy> Copies,
    ListingAnswer Answer,
    string? Failure = null)
{
    public static ShadowCopyListing Of(IReadOnlyList<ShadowCopy> copies) => new(copies, ListingAnswer.Listed);

    public static ShadowCopyListing Refused { get; } = new([], ListingAnswer.NeedsElevation);

    public static ShadowCopyListing Failed(string why) => new([], ListingAnswer.Failed, why);

    public bool Lists(Guid id) => Copies.Any(c => c.Id == id);
}
