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
public sealed record SquirrelVersionDirectory(string Path, string Name, Version Number);

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

    private IReadOnlyList<SquirrelInstallation>? _installations;

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
    /// The applications on this machine, memoised for the life of a planning pass (G4). Two
    /// providers, their presence probes and their §5.2 declarations all ask this same question of
    /// the same directory, and this is the one sweep behind all of them.
    /// </summary>
    public IReadOnlyList<SquirrelInstallation> Installations => _installations ??= Find();

    /// <summary>
    /// True where <c>%LOCALAPPDATA%</c> itself would not be listed, so <see cref="Installations"/>
    /// describes nothing rather than describing a machine with no Squirrel application on it.
    /// </summary>
    public bool ApplicationDataUnreadable { get; private set; }

    /// <summary>The applications whose own folder would not be listed, named so a plan can say so.</summary>
    public IReadOnlyList<string> UnreadableRoots { get; private set; } = [];

    /// <summary>Drop the memoised answer, so an application installed while the app was open is seen.</summary>
    public void Invalidate()
    {
        _installations = null;
        ApplicationDataUnreadable = false;
        UnreadableRoots = [];
    }

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
    private IReadOnlyList<SquirrelInstallation> Find(CancellationToken ct = default)
    {
        var scan = ChildDirectories.Under(_environment.LocalAppData);

        if (scan.Unreadable)
        {
            ApplicationDataUnreadable = true;
            return [];
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
            // silently is how the newest build gets named superseded.
            foreach (var candidate in inside.Directories.Concat(inside.Links))
            {
                if (VersionDirectory().Match(candidate.Name) is { Success: true } match)
                {
                    versions.Add(new SquirrelVersionDirectory(
                        LongPath.Display(candidate.FullName),
                        candidate.Name,
                        Version.Parse(match.Groups["version"].ValueSpan)));
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

        UnreadableRoots = refused;
        return found;
    }
}
