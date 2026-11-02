using System.Security;
using Deguffer.Core.Safety;
using Microsoft.Win32;

namespace Deguffer.Core.Profiles;

/// <summary>
/// This machine's profiles, read from the list Windows' own profile service keeps,
/// <c>HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList</c>, and removed by
/// Windows' own <c>DeleteProfileW</c>.
///
/// <para><b>The registry rather than WMI's <c>Win32_UserProfile</c>.</b> That class is a view of the same
/// key, so it knows nothing the key does not, and reading the key needs no COM and no elevation.</para>
///
/// <para><b>A profile is in use where its hive is loaded.</b> Windows loads a profile's hive under
/// <c>HKEY_USERS</c> while anything uses the profile, and every account can list that key. The entry's
/// <c>RefCount</c> is read as well, and either one saying "loaded" is enough.</para>
/// </summary>
public sealed class WindowsUserProfiles : IUserProfiles
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    private WindowsUserProfiles()
    {
    }

    public static WindowsUserProfiles Default { get; } = new();

    public ProfileListing List()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var list = hive.OpenSubKey(ProfileList);

            if (list is null)
            {
                return ProfileListing.Unread(PathPresence.Absent);
            }

            var folders = new ProfileFolders(
                list.GetValue("ProfilesDirectory") as string,
                list.GetValue("Default") as string,
                list.GetValue("Public") as string);
            var loaded = LoadedHives();

            return new ProfileListing(
                PathPresence.Present,
                folders,
                [.. list.GetSubKeyNames().Select(sid => Read(list, sid, loaded))]);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // Every account may read the list, so a refusal is unusual, and it is never evidence that a
            // profile is absent.
            return ProfileListing.Unread(PathPresence.Refused);
        }
    }

    public string? MachineAccountDomain() => ProfileCalls.MachineAccountDomain();

    public AccountReading LookUp(string sid) => ProfileCalls.LookUp(sid);

    public ProfileRemoval Remove(string sid) => ProfileCalls.Remove(sid);

    /// <summary>
    /// One entry. A value that cannot be read, or is not of the type Windows writes, says nothing, and
    /// never drops the entry: a profile missing from the list is one the §5.6 check could not see.
    /// </summary>
    private static UserProfile Read(RegistryKey list, string sid, IReadOnlySet<string>? loaded)
    {
        try
        {
            using var entry = list.OpenSubKey(sid);

            if (entry is null)
            {
                return new UserProfile(sid, Folder: null, ProfileLoad.Unknown, LastUsed: null);
            }

            var folder = entry.GetValue("ProfileImagePath") as string;
            var inUse = entry.GetValue("RefCount") is int count && count > 0;
            var load = inUse || loaded?.Contains(sid) is true
                ? ProfileLoad.Loaded
                : loaded is null ? ProfileLoad.Unknown : ProfileLoad.NotLoaded;

            return new UserProfile(
                sid,
                string.IsNullOrWhiteSpace(folder) ? null : folder,
                load,
                Latest(Time(entry, "LocalProfileLoadTime"), Time(entry, "LocalProfileUnloadTime")));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return new UserProfile(sid, Folder: null, ProfileLoad.Unknown, LastUsed: null);
        }
    }

    /// <summary>The hives loaded under <c>HKEY_USERS</c>, or null where Windows would not list them.</summary>
    private static HashSet<string>? LoadedHives()
    {
        try
        {
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);

            return users.GetSubKeyNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// A time Windows records as two <c>REG_DWORD</c> halves of a <c>FILETIME</c>, <c>name</c>High and
    /// <c>name</c>Low, or null where either is missing or not a number.
    /// </summary>
    private static DateTime? Time(RegistryKey entry, string name)
    {
        if (entry.GetValue(name + "High") is not int high || entry.GetValue(name + "Low") is not int low)
        {
            return null;
        }

        var ticks = ((long)(uint)high << 32) | (uint)low;

        return ticks <= 0 || ticks > DateTime.MaxValue.ToFileTimeUtc() ? null : DateTime.FromFileTimeUtc(ticks);
    }

    private static DateTime? Latest(DateTime? first, DateTime? second) =>
        first is { } a && second is { } b ? (a > b ? a : b) : first ?? second;
}
