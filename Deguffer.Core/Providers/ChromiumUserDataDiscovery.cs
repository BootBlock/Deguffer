using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One application's Chromium user-data folder, as found on disk.</summary>
/// <param name="Name">
/// The folder's own name, which is the application's name: Chromium's user-data folder is created
/// by the embedding application under its own vendor name, so this is the only label available and
/// it is the one the user will recognise in the folder listing.
/// </param>
/// <param name="Path">The folder, in display form — a plan never holds an extended-length path.</param>
/// <param name="Profiles">
/// The directories under it that a profile's caches may sit in: the folder itself, plus any
/// per-profile directory beside it. Both layouts are real. An application embedding the engine
/// keeps one profile and writes the caches into the user-data folder directly, while a
/// Chromium-derived host keeps <c>Default</c>, <c>Profile 1</c> and so on, each with its own copy.
/// </param>
public sealed record ChromiumUserData(string Name, string Path, IReadOnlyList<string> Profiles);

/// <summary>
/// Finds the Chromium user-data folders on this machine, one level under <c>%APPDATA%</c> and
/// <c>%LOCALAPPDATA%</c>.
///
/// <para>Separate from <see cref="ChromiumCacheProvider"/> because the two answer different
/// questions. This one answers "whose folder is this?", and the provider answers "what inside it
/// may go". Keeping them apart is what stops the second question from being asked of a folder that
/// never passed the first, which is precisely the failure a coincidental <c>GPUCache</c> would
/// cause.</para>
///
/// <para><b>Identification is a positive test, never a cache name.</b> A folder qualifies only if
/// it holds <see cref="IdentifyingFile"/>, which Chromium writes into the user-data folder it owns
/// and nothing else has reason to create. A folder that merely contains a directory called
/// <c>GPUCache</c> does not qualify, and an application that has somehow never written the file is
/// invisible here — reclaiming nothing being the safe direction to be wrong in.</para>
/// </summary>
public sealed partial class ChromiumUserDataDiscovery(IUserEnvironment environment)
{
    /// <summary>
    /// Chromium's own marker for a user-data folder. It holds the browser-wide settings and, on
    /// Windows, the DPAPI-wrapped key that decrypts the cookies and saved passwords beside it —
    /// which is why <see cref="ChromiumCacheProvider"/> also asserts it survived.
    /// </summary>
    public const string IdentifyingFile = "Local State";

    /// <summary>
    /// A Chromium host's additional profiles. A known word <em>and</em> a number, on Playwright's
    /// pattern: <c>Profile 1</c> qualifies, and <c>Profile backup</c> is not a profile this looks
    /// inside.
    /// </summary>
    [GeneratedRegex(@"\AProfile [0-9]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedProfile();

    /// <summary>
    /// Every Chromium user-data folder under the two application-data roots.
    ///
    /// <para>The roots hold hundreds of directories between them, so the order of the two checks is
    /// the performance design (G4): one file-existence check rejects almost every candidate, and
    /// only a folder that passes it is enumerated at all.</para>
    ///
    /// <para>A link one level under an application-data root is neither followed nor reported.
    /// Everywhere else in Deguffer a skipped link is named, because there it is a sibling of
    /// something being deleted and the user can see it in the folder. Here it is neither: this walk
    /// is choosing which applications to look at, not classifying the children of a tool root, and
    /// a link to some unrelated application's data folder is not something a plan would ever have
    /// mentioned. What it points at was never identified, so it is not looked at.</para>
    /// </summary>
    public IReadOnlyList<ChromiumUserData> Discover(CancellationToken ct = default)
    {
        var found = new List<ChromiumUserData>();

        foreach (var root in new[] { environment.RoamingAppData, environment.LocalAppData })
        {
            foreach (var child in ChildDirectories.Under(root).Directories)
            {
                ct.ThrowIfCancellationRequested();

                var path = LongPath.Display(child.FullName);

                if (!LongPath.FileExists(Path.Combine(path, IdentifyingFile)))
                {
                    continue;
                }

                found.Add(new ChromiumUserData(child.Name, path, ProfilesUnder(path)));
            }
        }

        return found;
    }

    /// <summary>
    /// The user-data folder itself, then its named profiles. The folder is always included because
    /// a Chromium host writes <c>GPUCache</c> there as well as inside each profile, and an
    /// application embedding the engine writes all of its caches there and has no profiles at all.
    /// </summary>
    private static IReadOnlyList<string> ProfilesUnder(string userData)
    {
        List<string> profiles = [userData];

        profiles.AddRange(ChildDirectories.Under(userData).Directories
            .Where(d => IsProfile(d.Name))
            .Select(d => LongPath.Display(d.FullName)));

        return profiles;
    }

    private static bool IsProfile(string name) =>
        name.Equals("Default", StringComparison.OrdinalIgnoreCase) || NumberedProfile().IsMatch(name);
}
