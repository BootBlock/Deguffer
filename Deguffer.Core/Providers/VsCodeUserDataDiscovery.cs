using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One Code - OSS editor's user-data folder, as found on disk.</summary>
/// <param name="Name">
/// The folder's own name, which is the editor's name: a Code - OSS derivative creates its user-data
/// folder under whatever <c>product.json</c> calls it — <c>Code</c>, <c>Code - Insiders</c>,
/// <c>VSCodium</c>, <c>Cursor</c>. It is the only label available and the one the user will
/// recognise in the folder listing.
/// </param>
/// <param name="Path">The folder, in display form — a plan never holds an extended-length path.</param>
public sealed record VsCodeUserData(string Name, string Path);

/// <summary>
/// Finds the Code - OSS editor user-data folders on this machine, one level under <c>%APPDATA%</c>.
///
/// <para>Separate from the two providers that read it because it answers a different question. This
/// one answers "whose folder is this?", and a provider answers "what inside it may go". Keeping
/// them apart is what stops the second question from being asked of a folder that never passed the
/// first — the same split <see cref="ChromiumUserDataDiscovery"/> exists for, and for the same
/// reason: <c>CachedData</c> is a name any directory anywhere may carry.</para>
///
/// <para><b>Identification is two positive tests, and neither of them is a cache name.</b> A folder
/// qualifies only if it holds Chromium's own <see cref="ChromiumUserDataDiscovery.IdentifyingFile"/>
/// <em>and</em> <see cref="IdentifyingFile"/>. The first says the folder is an Electron
/// application's user-data folder, which is the identification
/// <see cref="ChromiumCacheProvider"/> already acts on; the second says that application is a
/// Code - OSS editor, because the global storage database is what the editor's own storage service
/// creates there on first run and nothing else has reason to write. Requiring both means the two
/// providers that reach into this folder agree about which folders they may enter. An editor that
/// has somehow never written either file is invisible here, and reclaiming nothing is the safe
/// direction to be wrong in.</para>
///
/// <para><b>Roaming only, and that is Electron's rule rather than a guess.</b>
/// <c>app.getPath('userData')</c> resolves under <c>%APPDATA%</c> on Windows, so the local
/// application-data root is not walked. Packaged (MSIX) editors are out of reach here for the
/// reason <see cref="ChromiumCacheProvider"/> gives: Windows redirects their <c>%APPDATA%</c>, and
/// classifying that redirection is §3 of <c>docs/todo/unreached-locations.md</c>.</para>
/// </summary>
public sealed class VsCodeUserDataDiscovery(IUserEnvironment environment)
{
    /// <summary>
    /// The editor's own marker for its user-data folder: the global storage database, which the
    /// storage service opens or creates at startup. It holds the editor's machine-wide state, which
    /// is also why <see cref="NeverOffered"/> asserts that it survives.
    /// </summary>
    public static readonly string IdentifyingFile = Path.Combine("User", "globalStorage", "state.vscdb");

    /// <summary>
    /// What is never a candidate inside one of these folders, named in full so §5.6 asserts it
    /// rather than merely never mentioning it.
    ///
    /// <para><c>User</c> is §3's founding example of user data wearing a cache costume, and
    /// <c>_spec.md</c> §4.3 already says so: <c>workspaceStorage</c> was 11.6 GB on the measured
    /// machine, <c>globalStorage</c> 1.5 GB and <c>History</c> 1.1 GB, and between them they hold
    /// every extension's stored state, every open editor and terminal the editor will restore, and
    /// the local undo history of files that were never committed. The directory is classified Tier 4
    /// like any other unrecognised child, so this list adds nothing to what is <em>planned</em>. It
    /// adds what a run can show afterwards, which is the whole of §5.6: naming the largest things in
    /// there individually is what turns "we did not target it" into evidence that it is still on
    /// disk.</para>
    ///
    /// <para>Shared by both providers that read this folder, because the fact belongs to the
    /// editor's layout rather than to either provider's rules. A caches provider and a logs provider
    /// are two chances to write the same list, and one chance to write it once.</para>
    /// </summary>
    public static readonly (string RelativePath, string Reason)[] NeverOffered =
    [
        ("User",
            "The editor's own user folder. Settings, keybindings, snippets, profiles and every "
            + "extension's stored state live in here, and none of it is a cache."),
        (Path.Combine("User", "globalStorage"),
            "What every installed extension has stored — sign-ins, indexes and each extension's own "
            + "settings."),
        (Path.Combine("User", "workspaceStorage"),
            "The state of every workspace you have opened: the editors, terminals and view layout "
            + "the editor restores when you open it again."),
        (Path.Combine("User", "History"),
            "The editor's local undo history. For a file that was never committed it is the only "
            + "copy of what came before."),
        (Path.Combine("User", "profiles"),
            "The settings and extension state of every editor profile other than the default one."),
        (Path.Combine("User", "settings.json"), "The editor settings you have changed."),
        (Path.Combine("User", "keybindings.json"), "The keyboard shortcuts you have changed."),
        (IdentifyingFile,
            "The editor's own global storage database, which is also how Deguffer identified this "
            + "folder as an editor's at all."),
    ];

    /// <summary>
    /// Every Code - OSS editor user-data folder under <c>%APPDATA%</c>.
    ///
    /// <para>The root holds hundreds of directories, so the order of the two checks is the
    /// performance design (G4): one file-existence check rejects almost every candidate, and only a
    /// folder that passes it is asked the second question.</para>
    ///
    /// <para>A link one level under the application-data root is neither followed nor reported, on
    /// <see cref="ChromiumUserDataDiscovery"/>'s reasoning: this walk is choosing which editors to
    /// look at rather than classifying the children of a tool root, and a link to some unrelated
    /// application's data folder is not something a plan would ever have mentioned. What it points
    /// at was never identified, so it is not looked at.</para>
    /// </summary>
    public IReadOnlyList<VsCodeUserData> Discover(CancellationToken ct = default)
    {
        var found = new List<VsCodeUserData>();

        UnreadableRoots = [];

        var scan = ChildDirectories.Under(environment.RoamingAppData);

        if (scan.Unreadable)
        {
            // An application-data root that will not be listed leaves this walk with nothing to
            // report and nothing to say, which a provider would otherwise render as "no editor on
            // this machine keeps a cache here". It never looked.
            UnreadableRoots = [environment.RoamingAppData];
            return found;
        }

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(child.FullName);

            if (LongPath.FileExists(Path.Combine(path, ChromiumUserDataDiscovery.IdentifyingFile))
                && LongPath.FileExists(Path.Combine(path, IdentifyingFile)))
            {
                found.Add(new VsCodeUserData(child.Name, path));
            }
        }

        return found;
    }

    /// <summary>
    /// The application-data root the last <see cref="Discover"/> was refused, so a caller can avoid
    /// reporting "nothing found" as though the folder had been read. Empty on every ordinary
    /// machine: <c>%APPDATA%</c> sits inside the user's own profile.
    /// </summary>
    public IReadOnlyList<string> UnreadableRoots { get; private set; } = [];
}
