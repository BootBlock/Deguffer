using Deguffer.Core.Profiles;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// Stands in for Windows' profiles: a list the test writes, account answers the test chooses, and a
/// removal that takes the entry out of the list and deletes the folder it records, as
/// <c>DeleteProfileW</c> does.
///
/// <para><b>Injected everywhere, never defaulted.</b> The real one removes the profiles of the machine
/// that runs the suite, so every fixture that plans a removal passes one of these.</para>
/// </summary>
public sealed class FakeUserProfiles : IUserProfiles
{
    /// <summary>This machine's account domain in the fakes, which every local account's identifier begins with.</summary>
    public const string MachineDomain = "S-1-5-21-1111111111-2222222222-3333333333";

    /// <summary>Another domain's, for a domain account.</summary>
    public const string OtherDomain = "S-1-5-21-4444444444-5555555555-6666666666";

    /// <param name="profilesFolder">The folder profiles are kept in, which exists on disk.</param>
    public FakeUserProfiles(string profilesFolder)
    {
        ProfilesFolder = profilesFolder;
        Directory.CreateDirectory(profilesFolder);
    }

    public string ProfilesFolder { get; }

    /// <summary>Every entry in the list now. A removal takes one out.</summary>
    public List<UserProfile> Profiles { get; } = [];

    /// <summary>What the listing answers. Anything but present gives no entries.</summary>
    public PathPresence ListPresence { get; set; } = PathPresence.Present;

    /// <summary>What the machine's account domain is, or null where Windows does not say.</summary>
    public string? Domain { get; set; } = MachineDomain;

    /// <summary>What each account lookup answers, by identifier. An identifier not named here is not found.</summary>
    public Dictionary<string, AccountReading> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What each removal answers, by identifier. A profile not named here is removed.</summary>
    public Dictionary<string, ProfileRemoval> Removals { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Run after each removal is answered, so a test can stand in for what else changes then.</summary>
    public Action<string>? OnRemoved { get; set; }

    /// <summary>Every identifier Windows was asked to remove, in order.</summary>
    public List<string> Removed { get; } = [];

    /// <summary>
    /// A profile in the profiles folder named <paramref name="name"/>, with a file of
    /// <paramref name="bytes"/> bytes in it, recorded for the local account numbered <paramref name="rid"/>.
    /// </summary>
    public UserProfile AddLocal(int rid, string name, long bytes = 1000, ProfileLoad load = ProfileLoad.NotLoaded) =>
        Add($"{MachineDomain}-{rid}", name, bytes, load);

    /// <summary>A profile recorded for <paramref name="sid"/>, with its folder made on disk.</summary>
    public UserProfile Add(string sid, string name, long bytes = 1000, ProfileLoad load = ProfileLoad.NotLoaded)
    {
        var folder = Path.Combine(ProfilesFolder, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "NTUSER.DAT"), new byte[bytes]);

        return AddEntry(new UserProfile(sid, folder, load, new DateTime(2025, 3, 4, 0, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>An entry exactly as given, whether or not its folder exists.</summary>
    public UserProfile AddEntry(UserProfile profile)
    {
        Profiles.Add(profile);
        return profile;
    }

    public ProfileListing List() =>
        ListPresence is PathPresence.Present
            ? new ProfileListing(
                PathPresence.Present,
                new ProfileFolders(ProfilesFolder, Path.Combine(ProfilesFolder, "Default"), Path.Combine(ProfilesFolder, "Public")),
                [.. Profiles])
            : ProfileListing.Unread(ListPresence);

    public string? MachineAccountDomain() => Domain;

    public AccountReading LookUp(string sid) =>
        Accounts.TryGetValue(sid, out var reading) ? reading : new AccountReading(AccountAnswer.NotFound);

    public ProfileRemoval Remove(string sid)
    {
        Removed.Add(sid);

        var answer = Removals.TryGetValue(sid, out var given) ? given : new ProfileRemoval(ProfileRemovalAnswer.Removed);

        if (answer.Answer is ProfileRemovalAnswer.Removed && Profiles.Find(p => p.Sid == sid) is { } profile)
        {
            Profiles.Remove(profile);

            if (profile.Folder is { } folder && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        OnRemoved?.Invoke(sid);

        return answer;
    }
}
