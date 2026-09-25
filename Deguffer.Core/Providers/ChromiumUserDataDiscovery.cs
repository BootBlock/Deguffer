using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One application's Chromium user-data folder, as found on disk.</summary>
/// <param name="Name">
/// What the user knows the application as. For a declared browser that is its product name. For an
/// application embedding the engine it is the folder's own name: the embedding application creates
/// the folder under its own vendor name, so this is the only label available and it is the one the
/// user will recognise in the folder listing.
/// </param>
/// <param name="ProcessName">
/// The application's process, for §5.3's warning. A declared browser names it. For an embedding
/// application the folder's name stands in for it, which is right far more often than not for an
/// application that named its own data folder.
/// </param>
/// <param name="Path">The folder, in display form — a plan never holds an extended-length path.</param>
/// <param name="Profiles">
/// The directories under it that a profile's caches may sit in: the folder itself, plus any
/// per-profile directory beside it. Both layouts are real. An application embedding the engine
/// keeps one profile and writes the caches into the user-data folder directly, while a
/// Chromium-derived host keeps <c>Default</c>, <c>Profile 1</c> and so on, each with its own copy.
/// </param>
/// <param name="ProfilesIncomplete">
/// Whether the user-data folder refused to be listed, so <paramref name="Profiles"/> holds only the
/// folder itself and any profile beside it went unseen. Distinguishing this matters because a
/// refused folder produces exactly the one-entry list a single-profile embedder legitimately
/// produces, and a plan built on it counts fewer spared children than there are while saying the
/// count is what is left alone.
/// </param>
public sealed record ChromiumUserData(
    string Name,
    string ProcessName,
    string Path,
    IReadOnlyList<string> Profiles,
    bool ProfilesIncomplete = false);

/// <summary>
/// Finds the Chromium user-data folders on this machine: one level under <c>%APPDATA%</c> and
/// <c>%LOCALAPPDATA%</c>, where an application embedding the engine keeps its folder, and at each
/// place a <see cref="ChromiumBrowser"/> declares, where a browser keeps its own.
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
    /// Every Chromium user-data folder under the two application-data roots, then every declared
    /// browser's.
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

        UnreadableRoots = [];
        Obstructed = [];

        foreach (var root in new[] { environment.RoamingAppData, environment.LocalAppData })
        {
            var scan = ChildDirectories.Under(root);

            if (scan.Unreadable)
            {
                // An application-data root that will not be listed leaves this walk with nothing to
                // report and nothing to say, which the provider would otherwise render as "no
                // application on this machine keeps a Chromium cache". It never looked.
                UnreadableRoots = [.. UnreadableRoots, root];
                continue;
            }

            foreach (var child in scan.Directories)
            {
                ct.ThrowIfCancellationRequested();

                var path = LongPath.Display(child.FullName);

                if (!LongPath.FileExists(Path.Combine(path, IdentifyingFile)))
                {
                    continue;
                }

                var profiles = ProfilesUnder(path, out var incomplete);
                found.Add(new ChromiumUserData(child.Name, child.Name, path, profiles, incomplete));
            }
        }

        foreach (var browser in ChromiumBrowser.Declared)
        {
            ct.ThrowIfCancellationRequested();

            if (Identify(browser) is { } userData)
            {
                found.Add(userData);
            }
        }

        return found;
    }

    /// <summary>
    /// The declared browser's folder, if it holds <see cref="IdentifyingFile"/> and is reached
    /// without passing through a link.
    ///
    /// <para><b>A declared path is built, not enumerated, so no listing has filtered its links
    /// out.</b> The one-level walk above meets its only intermediate directory as a child of the
    /// root, and a link there is set aside before anything is looked at. Here the vendor directory,
    /// the product directory and the folder itself are joined from constants, and a junction at any
    /// of them would put every deletion on the far side while each §5.6 survivor resolved through
    /// the same link and passed. So every segment is checked before the marker is.</para>
    ///
    /// <para>An obstacle is recorded only where the browser may be behind it. A vendor directory
    /// moved onto another drive is ordinary, and naming it for a browser that was never installed
    /// would be a sentence about nothing. The marker is probed through the obstacle for that reason
    /// alone: it decides whether to say something, never whether to look inside.</para>
    /// </summary>
    private ChromiumUserData? Identify(ChromiumBrowser browser)
    {
        if (browser.PathIn(environment) is not (var root, var userData))
        {
            return null;
        }

        var marker = Path.Combine(userData, IdentifyingFile);

        if (DerivedPath.FirstObstacleBetween(root, userData) is { } obstacle)
        {
            if (LongPath.ProbeFile(marker) is not PathPresence.Absent)
            {
                Obstructed = [.. Obstructed, new ObstructedBrowser(browser, obstacle)];
            }

            return null;
        }

        if (!LongPath.FileExists(marker))
        {
            return null;
        }

        var path = LongPath.Display(userData);
        var profiles = ProfilesUnder(path, out var incomplete);
        return new ChromiumUserData(browser.Name, browser.ProcessName, path, profiles, incomplete);
    }

    /// <summary>
    /// The application-data roots the last <see cref="Discover"/> was refused, so a caller can avoid
    /// reporting "nothing found" as though the folders had been read. Empty on every ordinary
    /// machine: both roots sit inside the user's own profile.
    /// </summary>
    public IReadOnlyList<string> UnreadableRoots { get; private set; } = [];

    /// <summary>
    /// The declared browsers the last <see cref="Discover"/> did not look inside because a link, or
    /// a segment Windows would not describe, stood between the application-data root and the
    /// browser's folder. Empty on every ordinary machine.
    /// </summary>
    public IReadOnlyList<ObstructedBrowser> Obstructed { get; private set; } = [];

    /// <summary>
    /// The user-data folder itself, then its named profiles. The folder is always included because
    /// a Chromium host writes <c>GPUCache</c> there as well as inside each profile, and an
    /// application embedding the engine writes all of its caches there and has no profiles at all.
    /// </summary>
    /// <param name="incomplete">
    /// True where the user-data folder would not be listed. The folder itself is still returned,
    /// because the caches inside it are reached by name and a full path resolves through a directory
    /// the account may not list — but any <c>Default</c> or <c>Profile N</c> beside it was never
    /// seen. Without this the answer is one path, which is exactly what a legitimate single-profile
    /// embedder returns, and no caller could tell the two apart.
    /// </param>
    private static IReadOnlyList<string> ProfilesUnder(string userData, out bool incomplete)
    {
        List<string> profiles = [userData];

        var scan = ChildDirectories.Under(userData);
        incomplete = scan.Unreadable;

        profiles.AddRange(scan.Directories
            .Where(d => IsProfile(d.Name))
            .Select(d => LongPath.Display(d.FullName)));

        return profiles;
    }

    private static bool IsProfile(string name) =>
        name.Equals("Default", StringComparison.OrdinalIgnoreCase) || NumberedProfile().IsMatch(name);
}

/// <summary>A declared browser whose folder was not looked inside, and what stood in the way.</summary>
public sealed record ObstructedBrowser(ChromiumBrowser Browser, DerivedPathObstacle Obstacle);
