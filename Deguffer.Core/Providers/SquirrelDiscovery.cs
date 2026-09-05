using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One installed version of a Squirrel application, as it appears on disk.</summary>
/// <param name="Path">The directory, in display form.</param>
/// <param name="Name">Its own name, <c>app-3.6.4</c>, which is what the user sees in the folder.</param>
/// <param name="Number">
/// The version parsed out of that name. Squirrel resolves which build to launch by ordering these
/// same names, so ordering them is reading the application's own rule rather than inventing one.
/// </param>
/// <param name="IsLink">
/// Whether this is a junction or a symbolic link rather than a directory.
///
/// <para>Such a build is counted when the versions are ordered and never removed, and it needs both
/// halves. Dropping it from the ordering is how the newest build gets named superseded. Removing it
/// takes a link whose far side nobody classified — and the figure beside it was measured
/// <em>through</em> the link, so the row would promise the far side's size and reclaim none of
/// it.</para>
/// </param>
public sealed record SquirrelVersionDirectory(string Path, string Name, Version Number, bool IsLink);

/// <summary>
/// One Squirrel-installed application under <c>%LOCALAPPDATA%</c>, and the version directories in
/// it.
/// </summary>
/// <param name="Name">
/// The folder's own name, which is the application's: Squirrel installs into a directory named for
/// the package, so this is the only label available and the one the user will recognise.
/// </param>
/// <param name="Root">The application folder itself, in display form. Never a target.</param>
/// <param name="Versions">
/// The version directories whose version could be read, oldest first.
/// </param>
/// <param name="UnreadableVersionNames">
/// Children named like a version directory whose version could not be read — a pre-release build
/// such as <c>app-2.0.0-beta1</c>. Kept rather than dropped because their presence is what makes
/// <see cref="Superseded"/> empty: see the property for why that has to fail closed.
/// </param>
public sealed record SquirrelInstallation(
    string Name,
    string Root,
    IReadOnlyList<SquirrelVersionDirectory> Versions,
    IReadOnlyList<string> UnreadableVersionNames)
{
    /// <summary>
    /// The newest installed version, which is the one the application launches, or null where the
    /// set could not be ordered.
    /// </summary>
    public SquirrelVersionDirectory? Current =>
        UnreadableVersionNames.Count > 0 ? null : Versions.LastOrDefault();

    /// <summary>
    /// The versions an update left behind, which is every one that is not <see cref="Current"/>.
    ///
    /// <para><b>Empty as soon as one version directory could not be read, and that is the whole
    /// safety property here.</b> A pre-release version orders below its own release under one
    /// reading and above it under another, so an installation holding <c>app-1.2.3</c> beside
    /// <c>app-1.3.0-beta1</c> has no answer this can give. Ordering the readable ones and calling
    /// the highest of them current would then name the <em>running</em> build superseded, and
    /// removing it leaves the user without the application.</para>
    /// </summary>
    public IReadOnlyList<SquirrelVersionDirectory> Superseded =>
        Current is { } current ? [.. Versions.Where(v => v != current)] : [];
}

/// <summary>
/// What one look at <c>%LOCALAPPDATA%</c> found, with what it could not read carried beside it.
///
/// <para>The three travel together rather than as three properties on the discovery, and that is
/// the point: two of them are only known <em>after</em> the sweep has run, so a caller holding a
/// lazily-memoised property could read them before it and see the defaults. A provider then reports
/// an application folder it could not list as though the refusal had not happened, which is the
/// "a safeguard that could not run must not look like a safeguard that found nothing" case.</para>
/// </summary>
/// <param name="Installations">The applications found, in the order the profile listed them.</param>
/// <param name="ApplicationDataUnreadable">
/// <c>%LOCALAPPDATA%</c> itself would not be listed, so <paramref name="Installations"/> describes
/// nothing rather than describing a machine with no Squirrel application on it.
/// </param>
/// <param name="UnreadableRoots">
/// Application folders that hold the updater and then refused to be listed. A folder can pass the
/// first half of the identification test and fail the second, because a listing right is separate
/// from a traverse right — and it is not an application with no builds.
/// </param>
public sealed record SquirrelSweep(
    IReadOnlyList<SquirrelInstallation> Installations,
    bool ApplicationDataUnreadable,
    IReadOnlyList<string> UnreadableRoots);

/// <summary>
/// Finds the applications the Squirrel updater installed, one level under <c>%LOCALAPPDATA%</c>.
///
/// <para>Separate from the providers for the reason <see cref="ChromiumUserDataDiscovery"/> is: one
/// type answers "whose folder is this?" and the others answer "what inside it may go", and keeping
/// them apart is what stops the second question from being asked of a folder that never passed the
/// first. Two providers share this one answer, because the staging Squirrel leaves behind and the
/// versions it superseded are different tiers over the same set of folders.</para>
///
/// <para><b>Identification is two positive tests together, and neither is a cache name.</b> A folder
/// qualifies only if it holds <see cref="UpdaterName"/>, the updater Squirrel installs beside every
/// application it manages, <em>and</em> a child named for a version Deguffer could read. Either
/// alone is too weak: other software ships a file called <c>Update.exe</c>, and a folder holding a
/// directory whose name begins <c>app-</c> is not evidence of an updater. An application that fails
/// the pair is invisible here, and reclaiming nothing is the safe direction to be wrong in.</para>
/// </summary>
public sealed partial class SquirrelDiscovery
{
    /// <summary>
    /// The updater itself, which Squirrel installs in the root of every application it manages and
    /// which nothing here may ever remove. It is half of the identification test above.
    /// </summary>
    public const string UpdaterName = "Update.exe";

    /// <summary>Squirrel's shared staging folder, beside the applications rather than inside one.</summary>
    public const string StagingDirectoryName = "SquirrelTemp";

    /// <summary>
    /// Set by the user to move the staging folder somewhere else. Squirrel reads it before it falls
    /// back to <see cref="StagingDirectoryName"/> under <c>%LOCALAPPDATA%</c>, so §5.2's "never
    /// assume a location" applies to this root exactly as it does to Playwright's.
    /// </summary>
    public const string StagingVariable = "SQUIRREL_TEMP";

    /// <summary>The folder each application keeps its downloaded update packages in.</summary>
    public const string PackagesDirectoryName = "packages";

    /// <summary>
    /// An installed version directory: the literal prefix Squirrel writes, and a version that can be
    /// read. A known word <em>and</em> a number, on <see cref="PlaywrightBrowsersProvider"/>'s
    /// pattern — <c>app-3.6.4</c> qualifies and <c>app.ico</c> does not.
    ///
    /// <para>Two to four components, because that is what <see cref="Version"/> reads. A pre-release
    /// suffix is deliberately outside the pattern; see <see cref="SquirrelInstallation.Superseded"/>
    /// for what an installation does about one.</para>
    /// </summary>
    [GeneratedRegex(
        @"\Aapp-(?<version>[0-9]+(?:\.[0-9]+){1,3})\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionDirectory();

    /// <summary>
    /// Anything named like a version directory, whether or not its version can be read. The second
    /// character is required so that a directory called exactly <c>app-</c> is not counted as a
    /// version nobody could read.
    /// </summary>
    [GeneratedRegex(@"\Aapp-.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedLikeAVersion();

    private readonly IUserEnvironment _environment;

    private SquirrelSweep? _sweep;

    public SquirrelDiscovery(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _environment = environment;

        ConfiguredStagingRoot =
            environment.GetEnvironmentVariable(StagingVariable)?.Trim() is { Length: > 0 } value
                ? value
                : null;

        StagingRoot = ConfiguredStagingRoot is { } configured
            ? LongPath.Configured(configured)
            : Path.Combine(environment.LocalAppData, StagingDirectoryName);
    }

    /// <summary>
    /// What <see cref="StagingVariable"/> was set to, trimmed, or null where it is unset — which is
    /// the ordinary machine. Read once, because a provider names it in the sentence it writes about
    /// a value it could not make sense of.
    /// </summary>
    public string? ConfiguredStagingRoot { get; }

    /// <summary>
    /// Squirrel's shared staging folder, independent of any one application.
    ///
    /// <para>Null where <see cref="StagingVariable"/> is set to something that is not a full path.
    /// Squirrel resolves a relative value against whichever process is updating, which Deguffer is
    /// not — so there is no correct interpretation available, and offering nothing is the only
    /// honest answer. This is <see cref="PlaywrightBrowsersProvider.ResolveRoot"/>'s reasoning, and
    /// <see cref="LongPath.Configured"/> normalises what it does accept, so a trailing separator or
    /// a <c>..</c> cannot reach an enumeration.</para>
    /// </summary>
    public string? StagingRoot { get; }

    /// <summary>
    /// One look at <c>%LOCALAPPDATA%</c>, memoised for the life of a planning pass (G4). Two
    /// providers, their presence probes and their §5.2 declarations all ask this same question of
    /// the same directory, and this is the one sweep behind all of them.
    ///
    /// <para>A method rather than a property, on <see cref="ChromiumCacheProvider.Applications"/>'s
    /// pattern, because the memoisation must not cost the cancellation: what it does on a miss is a
    /// child listing of a root holding hundreds of directories, plus a listing per candidate below
    /// it, and G4 requires a scan the user can abandon.</para>
    /// </summary>
    public SquirrelSweep Look(CancellationToken ct = default) => _sweep ??= Find(ct);

    /// <summary>Drop the memoised answer, so an application installed while the app was open is seen.</summary>
    public void Invalidate() => _sweep = null;

    /// <summary>
    /// The sweep. One child-directory listing of <c>%LOCALAPPDATA%</c>, then one file-existence
    /// check per child, and only a child that passes it is enumerated at all — the order is the
    /// performance design (G4), because that root holds hundreds of directories on an ordinary
    /// machine.
    ///
    /// <para>A link one level under <c>%LOCALAPPDATA%</c> is neither followed nor reported, on
    /// <see cref="ChromiumUserDataDiscovery"/>'s reasoning: this walk is choosing which applications
    /// to look at rather than classifying the children of a tool root, so a link to some unrelated
    /// folder is not something a plan would ever have mentioned.</para>
    /// </summary>
    private SquirrelSweep Find(CancellationToken ct)
    {
        var scan = ChildDirectories.Under(_environment.LocalAppData);

        if (scan.Unreadable)
        {
            return new SquirrelSweep([], ApplicationDataUnreadable: true, []);
        }

        var found = new List<SquirrelInstallation>();
        var refused = new List<string>();

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var root = LongPath.Display(child.FullName);

            if (!LongPath.FileExists(Path.Combine(root, UpdaterName)))
            {
                continue;
            }

            var inside = ChildDirectories.Under(root);

            if (inside.Unreadable)
            {
                // The updater was found by name, and a listing right is separate from a traverse
                // right — so this folder can pass the first test and then refuse the second. It is
                // not an application with no versions, and a plan must not describe it as one.
                refused.Add(root);
                continue;
            }

            var versions = new List<SquirrelVersionDirectory>();
            var unreadable = new List<string>();

            // Links are read for their names and never followed. A version directory somebody moved
            // to another drive is still evidence of which version is current, and dropping it
            // silently is how the newest build gets named superseded. Which of the two a build is
            // travels with it, because a caller has to count it and then refuse it.
            foreach (var (candidate, isLink) in
                inside.Directories.Select(d => (d, false)).Concat(inside.Links.Select(l => (l, true))))
            {
                // TryParse rather than Parse. The pattern bounds the shape of a version and not its
                // magnitude, so a directory named app-9999999999 matches and then overflows — and
                // an exception out of here escapes the presence probe and takes down the planning
                // pass for every other provider too. A number nobody could read is exactly what
                // UnreadableVersionNames is for, and failing into it fails closed.
                if (VersionDirectory().Match(candidate.Name) is { Success: true } match
                    && Version.TryParse(match.Groups["version"].ValueSpan, out var number))
                {
                    versions.Add(new SquirrelVersionDirectory(
                        LongPath.Display(candidate.FullName), candidate.Name, number, isLink));
                }
                else if (NamedLikeAVersion().IsMatch(candidate.Name))
                {
                    unreadable.Add(candidate.Name);
                }
            }

            if (versions.Count == 0)
            {
                // The other half of the identification test. A folder with an updater in it and no
                // version directory Deguffer could read is not established as a Squirrel
                // application, so nothing under it is offered — its packages folder included.
                continue;
            }

            versions.Sort((a, b) => a.Number.CompareTo(b.Number));

            found.Add(new SquirrelInstallation(child.Name, root, versions, unreadable));
        }

        return new SquirrelSweep(found, ApplicationDataUnreadable: false, refused);
    }
}
