using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One profile of a Mozilla application, as <c>profiles.ini</c> names it.</summary>
/// <param name="Name">
/// The profile's own name from the file — <c>default-release</c> on an ordinary installation. It is
/// what the user sees in Firefox's own profile manager, so it is the label a plan should use.
/// </param>
/// <param name="RoamingPath">
/// The half under <c>%APPDATA%</c>, which holds bookmarks, logins, history and preferences. Nothing
/// in Deguffer ever targets anything inside it; it is carried so the provider can assert it survived.
/// </param>
/// <param name="LocalPath">
/// The half under <c>%LOCALAPPDATA%</c>, which holds the disk cache and the other regenerable
/// files. It is derived rather than read: Mozilla resolves the same relative path against both
/// application-data roots, so a profile recorded as <c>Profiles/xxxx.default-release</c> has its
/// caches at the matching place under the local root. The directory may not exist — a profile that
/// has never been opened has no local half at all.
/// </param>
public sealed record MozillaProfile(string Name, string RoamingPath, string LocalPath);

/// <summary>
/// Reads a Mozilla application's <c>profiles.ini</c> and resolves each profile's two halves.
///
/// <para>Separate from the provider for the reason <see cref="ChromiumUserDataDiscovery"/> is: this
/// answers "which directories are Firefox's profiles?", and the provider answers "what inside one of
/// them may go". Keeping them apart is what stops the second question being asked of a directory
/// that never passed the first — and here the first question has a single positive answer,
/// <c>profiles.ini</c>, which is Mozilla's own register of its profiles and the counterpart of
/// Chromium's <c>Local State</c>.</para>
///
/// <para>The application directory is a constructor argument rather than a constant because
/// Thunderbird keeps the identical layout under <c>%APPDATA%\Thunderbird</c> — the same
/// <c>profiles.ini</c>, the same two roots, the same <c>cache2</c>. Nothing here knows the name of
/// any application, so a second one is a second provider and no change to this file.</para>
/// </summary>
/// <param name="environment">The two application-data roots, behind the seam that makes this testable.</param>
/// <param name="applicationDirectory">
/// The application's own directory relative to each application-data root — <c>Mozilla\Firefox</c>
/// for Firefox.
/// </param>
public sealed partial class MozillaProfileDiscovery(IUserEnvironment environment, string applicationDirectory)
{
    /// <summary>
    /// Mozilla's own register of the profiles it owns. A directory named here is a Firefox profile;
    /// a directory that merely sits beside one is not, however it is named.
    /// </summary>
    public const string ProfilesFile = "profiles.ini";

    /// <summary>
    /// A profile section, which is a known word and a number — <see cref="ChromiumUserDataDiscovery"/>'s
    /// pattern, and Playwright's before it. It matters here because <c>profiles.ini</c> also carries
    /// <c>[General]</c> and one <c>[InstallXXXXXXXX]</c> section per installed Firefox, and an
    /// install section holds a <c>Default=Profiles/…</c> key of exactly the shape read below.
    /// </summary>
    [GeneratedRegex(@"\AProfile[0-9]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfileSection();

    /// <summary>Where <c>profiles.ini</c> and the user-data half of every profile live.</summary>
    public string RoamingRoot { get; } = Path.Combine(environment.RoamingAppData, applicationDirectory);

    /// <summary>Where the caches live, and the only root this provider ever deletes from.</summary>
    public string LocalRoot { get; } = Path.Combine(environment.LocalAppData, applicationDirectory);

    /// <summary>The register itself, which is also what identifies the installation as Mozilla's.</summary>
    public string ProfilesPath => Path.Combine(RoamingRoot, ProfilesFile);

    /// <summary>
    /// True where <c>profiles.ini</c> is on disk but would not be read, so the last
    /// <see cref="Discover"/> found no profiles without having established that there are none.
    ///
    /// Distinguished for the reason <see cref="ChromiumUserDataDiscovery.UnreadableRoots"/> is: a
    /// provider reporting "Firefox is not installed" about a file it was refused has stated
    /// something nobody checked.
    /// </summary>
    public bool ProfilesUnreadable { get; private set; }

    /// <summary>
    /// Profiles whose <c>profiles.ini</c> entry is an absolute path rather than a relative one, so
    /// they were not resolved and not looked at.
    ///
    /// <para>A profile the user moved elsewhere by hand keeps its cache in a directory this cannot
    /// derive: Mozilla's two-root split is a property of the <em>relative</em> form, and where the
    /// path is absolute the application uses that one directory for both halves. The caches then sit
    /// among <c>places.sqlite</c> and <c>logins.json</c> rather than in a tree of their own, which is
    /// a different safety argument from the one this provider makes. So such a profile is reported
    /// and skipped, which is the direction §5.2 requires the case nobody established to fail in.</para>
    /// </summary>
    public IReadOnlyList<string> ProfilesElsewhere { get; private set; } = [];

    /// <summary>
    /// Every profile <c>profiles.ini</c> names, with both of its halves resolved.
    ///
    /// <para>A profile is returned whether or not its local half is on disk. The local half is what
    /// gets cleaned and its absence means there is nothing to clean, but the roaming half is what
    /// §5.2 has to keep out of reach either way — and a profile that has never been opened is
    /// exactly the one whose bookmarks a user would least expect to lose.</para>
    /// </summary>
    public IReadOnlyList<MozillaProfile> Discover(CancellationToken ct = default)
    {
        ProfilesUnreadable = false;
        ProfilesElsewhere = [];

        if (!LongPath.FileExists(ProfilesPath))
        {
            return [];
        }

        if (ReadRegister() is not { } lines)
        {
            ProfilesUnreadable = true;
            return [];
        }

        var found = new List<MozillaProfile>();
        var elsewhere = new List<string>();

        foreach (var entry in Sections(lines, ct))
        {
            if (!entry.TryGetValue("Path", out var declared) || declared.Length == 0)
            {
                continue;
            }

            var name = entry.TryGetValue("Name", out var named) && named.Length > 0 ? named : declared;

            // IsRelative is absent on a hand-edited file, and the safe reading of a missing value is
            // the one that resolves nothing: an absolute path treated as relative would be appended
            // to the application root and land on a directory nobody named.
            if (!entry.TryGetValue("IsRelative", out var relative) || relative != "1")
            {
                elsewhere.Add(name);
                continue;
            }

            // The file records the separator Mozilla writes rather than the one Windows uses.
            var segments = declared.Replace('/', Path.DirectorySeparatorChar);

            if (Resolve(RoamingRoot, segments) is not { } roaming
                || Resolve(LocalRoot, segments) is not { } local)
            {
                // A relative path that climbs back out of the application's own directory. This is a
                // text file on disk, so the value is a claim rather than a fact, and the only
                // handling of a claim that would move the whole operation elsewhere is to refuse it.
                elsewhere.Add(name);
                continue;
            }

            found.Add(new MozillaProfile(name, roaming, local));
        }

        ProfilesElsewhere = elsewhere;
        return found;
    }

    /// <summary>
    /// <paramref name="relative"/> under <paramref name="root"/>, or null where it does not stay
    /// inside it.
    /// </summary>
    private static string? Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
        {
            return null;
        }

        var combined = LongPath.Configured(Path.Combine(root, relative));

        return combined is not null
            && combined.Length > root.Length
            && LongPath.Contains(root, combined)
                ? combined
                : null;
    }

    /// <summary>
    /// The key-value pairs of each <c>[ProfileN]</c> section. Everything else in the file — the
    /// <c>[General]</c> preferences and one <c>[Install…]</c> section per installed Firefox — is
    /// skipped rather than parsed.
    /// </summary>
    private static IEnumerable<Dictionary<string, string>> Sections(string[] lines, CancellationToken ct)
    {
        Dictionary<string, string>? current = null;

        foreach (var raw in lines)
        {
            ct.ThrowIfCancellationRequested();

            var line = raw.Trim();

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (current is not null)
                {
                    yield return current;
                }

                current = ProfileSection().IsMatch(line[1..^1])
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : null;
                continue;
            }

            var split = line.IndexOf('=');

            if (current is null || split <= 0)
            {
                continue;
            }

            current[line[..split].TrimEnd()] = line[(split + 1)..].TrimStart();
        }

        if (current is not null)
        {
            yield return current;
        }
    }

    private string[]? ReadRegister()
    {
        try
        {
            return File.ReadAllLines(LongPath.Extended(ProfilesPath));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A file the account may not read is §5.3's ordinary case rather than an error. It is
            // reported through ProfilesUnreadable rather than swallowed.
            return null;
        }
    }
}
