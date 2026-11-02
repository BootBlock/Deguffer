using Deguffer.Core.Safety;

namespace Deguffer.Core.Profiles;

/// <summary>
/// One entry in Windows' list of profiles, the <c>ProfileList</c> key, as Windows records it.
/// </summary>
/// <param name="Sid">
/// The entry's key name. Windows names each entry after the account's security identifier, and names
/// the copy it keeps of a profile it could not load after the same identifier with <c>.bak</c> added,
/// so this is not always a valid identifier.
/// </param>
/// <param name="Folder">
/// The folder Windows records for the profile, its <c>ProfileImagePath</c> expanded, or null where the
/// entry names none Deguffer could read.
/// </param>
/// <param name="Load">Whether Windows has the profile's registry hive loaded.</param>
/// <param name="LastUsed">
/// When Windows last loaded or unloaded the profile, in UTC, or null where it recorded neither.
/// </param>
public sealed record UserProfile(string Sid, string? Folder, ProfileLoad Load, DateTime? LastUsed);

/// <summary>Whether Windows has a profile's registry hive loaded, which it does while anything uses the profile.</summary>
public enum ProfileLoad
{
    NotLoaded,

    Loaded,

    /// <summary>Windows did not say. A profile in this state is treated as in use.</summary>
    Unknown,
}

/// <summary>
/// Where Windows keeps profiles, as the <c>ProfileList</c> key records it: the folder new profiles go
/// in, and the two profiles in it that belong to no account.
/// </summary>
/// <param name="Profiles">The <c>ProfilesDirectory</c> value, expanded, or null where Windows did not say.</param>
/// <param name="Default">The profile every new profile is copied from, or null where Windows did not say.</param>
/// <param name="Public">The profile every account shares, or null where Windows did not say.</param>
public sealed record ProfileFolders(string? Profiles, string? Default, string? Public)
{
    public static ProfileFolders None { get; } = new(null, null, null);
}

/// <summary>
/// Every entry in Windows' list of profiles, read at one moment.
/// </summary>
/// <param name="Presence">
/// <see cref="PathPresence.Present"/> where Windows' list was read. <see cref="PathPresence.Refused"/>
/// where Windows would not let it be read, which is never evidence that a profile is absent.
/// </param>
public sealed record ProfileListing(PathPresence Presence, ProfileFolders Folders, IReadOnlyList<UserProfile> Profiles)
{
    /// <summary>A listing Windows did not give.</summary>
    public static ProfileListing Unread(PathPresence presence) => new(presence, ProfileFolders.None, []);

    /// <summary>The entry named <paramref name="sid"/>, or null where the listing holds none.</summary>
    public UserProfile? Find(string sid) =>
        Profiles.FirstOrDefault(profile => string.Equals(profile.Sid, sid, StringComparison.OrdinalIgnoreCase));
}
