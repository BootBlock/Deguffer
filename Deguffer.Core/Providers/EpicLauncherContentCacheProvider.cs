using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The storefront artwork the Epic Games launcher keeps outside anybody's profile (497 MB in 3,917
/// flat files on the measured machine, 3,912 of them JPEGs).
///
/// <para><b>The launcher keeps a second data directory, and the two shipped Epic providers cannot
/// see it.</b> <see cref="EpicLauncherWebCacheProvider"/> and <see cref="EpicLauncherLogProvider"/>
/// both work under <c>%LOCALAPPDATA%</c>, so nothing in Deguffer reached
/// <c>%PROGRAMDATA%\Epic\EpicGamesLauncher\Data</c> until this. That is where the store's pictures
/// land, and on the measured machine their timestamps ran from 2022-11 to 2026-09 with no eviction
/// of any kind — four years of artwork for games the account may no longer show.</para>
///
/// <para><b>Tier 1, on the same argument the web cache row already makes.</b> Every file in there is
/// a picture Epic's servers still hold, fetched on demand and fetched again the next time the store
/// page is drawn. Nothing else produced it and nothing else is the authority for it.</para>
///
/// <para><b>§5.2, and the sibling here is unusually consequential.</b> <c>Manifests</c> beside the
/// cache is the launcher's record of which games are installed, so removing it makes the launcher
/// forget an installed library, and <c>VaultCache</c> a level up is downloaded game data the
/// launcher keeps deliberately. So this provider names one absolute path and reaches nothing else —
/// <see cref="DeclaredLocation"/>'s stricter form of §5.2, where the unrecognised case cannot arise
/// because there is no enumeration through which a sibling could be reached. The siblings are then
/// named again in <see cref="DeclaredRoot.ProtectedNames"/>, so a run produces evidence that they
/// survived rather than merely never mentioning them.</para>
///
/// <para><b><c>EMS</c> is deliberately left out.</b> It is 79 MB of promotional images sitting beside
/// <c>.layout</c>, <c>.sdmeta</c> and <c>.ini</c> panel metadata, and nobody has established what
/// that metadata is for. §5.2's answer to that is Tier 4, and 79 MB does not justify guessing. It is
/// named as a survivor rather than left unmentioned, for the reason above.</para>
///
/// <para><b>§5.1 has nothing to prefer.</b> Epic documents no cache-clearing route for this
/// directory, and the launcher exposes no command that empties it. The store's own settings reach
/// the profile's web cache and not this.</para>
///
/// <para><b>No administrator rights, and that was measured rather than assumed.</b> Being under
/// <c>%PROGRAMDATA%</c> is not the question <see cref="CleanupStep.RequiresElevation"/> asks: that
/// flag means the step can be seen and cannot be performed. Epic's installer writes an explicit,
/// non-inherited <c>BUILTIN\Users:(OI)(CI)(F)</c> onto <c>%PROGRAMDATA%\Epic</c>, which inherits
/// down to every file in the cache, so the signed-in user may remove it as they are. Windows' own
/// default for the directory is <c>Users:(OI)(CI)(RX)</c> plus create-only, which is what
/// <see cref="CrashDumpProvider"/>'s <c>WER</c> folders still carry and why those genuinely do
/// declare it.</para>
///
/// <para>Declaring it anyway would not have been the cautious choice. The shell refuses to tick a
/// row whose step needs elevation, so it would send somebody through a relaunch to reclaim half a
/// gigabyte they could already reclaim, and the plan's note would tell them something untrue about
/// their disk. On a machine whose permissions really are the restrictive default the removal fails
/// loudly, reclaims nothing, and leaves §5.6 passing — which is the direction to be wrong in.</para>
/// </summary>
public sealed class EpicLauncherContentCacheProvider : CleanupProviderBase
{
    /// <summary>The launcher's own directory under <c>%PROGRAMDATA%\Epic</c>.</summary>
    private const string LauncherDirectory = "EpicGamesLauncher";

    /// <summary>The folder inside it that holds the launcher's machine-wide working data.</summary>
    private static readonly string DataDirectory = Path.Combine(LauncherDirectory, "Data");

    private readonly IReadOnlyList<DeclaredRoot> _roots;

    public EpicLauncherContentCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _roots = Declare(system ?? SystemDirectories.Current);
    }

    public override string Id => "epic-launcher-content-cache";

    public override string Name => "Epic Games launcher store artwork";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The store downloads each picture again the first time the page showing it is opened, so "
        + "the storefront fills in more slowly once. Your installed games, your library and your "
        + "sign-in are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Epic Games launcher",
        Publisher = "Epic Games",
        Purpose = "The launcher saves every piece of storefront artwork it has ever drawn — the "
            + "cover images, screenshots and banners behind the store pages — into a shared folder "
            + "outside anybody's profile. Nothing ever removes one, so the folder holds artwork for "
            + "games that left the store years ago as well as for the page you opened this morning.",
        Recommendation = "The pictures come from Epic's servers and are fetched again on demand, so "
            + "this is safe to clear. It sits beside the launcher's record of which games you have "
            + "installed, which is why Deguffer removes only the one folder it recognises.",
    };

    /// <summary>
    /// What this provider names, root by root. Exposed so tests can assert that the root is never a
    /// target and that the consequential siblings are asserted rather than merely omitted.
    /// </summary>
    public IReadOnlyList<DeclaredRoot> Roots => _roots;

    /// <summary>
    /// §5.3, and the process is the launcher itself — the same one
    /// <see cref="EpicLauncherSaved.ProcessNames"/> names for the profile's folders. Shared rather
    /// than restated because it is a fact about the launcher rather than about either directory: a
    /// running launcher is writing artwork into this folder as the store is browsed, and anything it
    /// holds open is left in place.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => EpicLauncherSaved.ProcessNames;

    /// <summary>
    /// Presence is the cache folder itself being there. <c>%PROGRAMDATA%\Epic</c> exists on every
    /// machine that has ever run an Epic installer, the Unreal Engine launcher and Epic Online
    /// Services included, so reading the root as a hit would report this source on machines that
    /// have never opened the store.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(DeclaredPaths().Any(LongPath.DirectoryExists));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var scan = DeclaredLocations.Examine(_roots, ct);

        if (scan.FoundNothing)
        {
            return EmptyPlan(
                "The Epic Games launcher has kept no storefront artwork in the machine-wide folder.");
        }

        var notes = new List<PlanNote>(scan.Notes);

        var (steps, measured) = await PlanDeletionsAsync(scan.Targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        // §5.3, and only where something is actually going to be removed. A warning that the
        // launcher is holding files open, on a row with nothing to delete, describes a clean that
        // will not happen.
        if (steps.Count > 0 && BuildRunningProcessNote() is { } warning)
        {
            notes.Add(warning);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = Protect([.. scan.Protected]),
            Notes = notes,
            Fallback = measured.Fallback,
            WasNotExamined = scan.NothingWasExamined,
        };
    }

    /// <summary>
    /// One location, and the neighbours §5.6 must show a rule reaching into this folder did not
    /// reach.
    ///
    /// <para>The root is <c>%PROGRAMDATA%\Epic</c> rather than the <c>Data</c> folder two levels
    /// down, so that <c>VaultCache</c> — which sits beside <c>Data</c> and not inside it — is a named
    /// survivor of this provider rather than merely something it never mentions. The walk down to
    /// the target protects and link-checks each directory it passes through, so the depth costs
    /// nothing.</para>
    ///
    /// <para>Two files are named alongside the directories because child classification enumerates
    /// directories: anything that is a file is never seen and never asserted unless a provider names
    /// it. <c>Launcher.manifest</c> and its <c>.meta</c> are the launcher's own record of the build
    /// it is running, and an assertion that <c>Data</c> survived would pass with both of them
    /// gone.</para>
    ///
    /// <para><b>The list reaches the root's own children, not only the launcher's.</b> Rooting at
    /// <c>Epic</c> to name <c>VaultCache</c> and then asserting nothing outside
    /// <c>EpicGamesLauncher</c> would leave the other products under that root as things this
    /// provider merely never mentions — the position §5.6 exists to move a path out of.
    /// <c>UnrealEngineLauncher</c> holds <c>LauncherInstalled.dat</c>, which is the machine's record
    /// of where its Epic games are installed and is what other launchers read to find them.</para>
    ///
    /// <para><c>ReportsAge</c> is off, on <see cref="CleanupStep.LastWritten"/>'s rule that a whole
    /// cache leaves it null. The measured folder's timestamps ran across four years, and its newest
    /// child is whichever store page was drawn last — so §7's column would say "today" about a cache
    /// that is mostly ancient. That is Maven's problem arriving from the other end, and §7 renders a
    /// null as unknown rather than as an age.</para>
    /// </summary>
    private IReadOnlyList<DeclaredRoot> Declare(ISystemDirectories system) =>
    [
        new DeclaredRoot(
            Path.Combine(system.ProgramData, "Epic"),
            "The launcher's machine-wide folder must survive — only the storefront artwork cache "
            + "inside it is removed.",
            RequiresElevation: false,
            [
                new DeclaredLocation(
                    Path.Combine(DataDirectory, "ContentCache"),
                    "Cover images, screenshots and banners the store has drawn. Each one is fetched "
                    + "from Epic again the next time a page showing it is opened.",
                    ReportsAge: false),
            ],
            [
                (Path.Combine(DataDirectory, "Manifests"),
                    "The launcher's record of which games are installed and where. Removing it makes "
                    + "the launcher forget your installed library."),
                (Path.Combine(DataDirectory, "ManifestTemp"),
                    "Where the launcher assembles a new installation record before it replaces the "
                    + "one above."),
                (Path.Combine(LauncherDirectory, "VaultCache"),
                    "Downloaded game data the launcher is keeping on purpose, which is not a cache "
                    + "of anything it can fetch back for free."),
                (Path.Combine(DataDirectory, "DownloadManager"),
                    "The state of downloads that are part-finished. Removing it loses the progress "
                    + "of anything still downloading."),
                (Path.Combine(DataDirectory, "Update"),
                    "The launcher's own in-flight update state."),
                (Path.Combine(DataDirectory, "EMS"),
                    "Promotional panels, and the layout and metadata files describing them. Nobody "
                    + "has established what that metadata is for, so §5.2 leaves it alone."),
                (Path.Combine(DataDirectory, "Catalog"),
                    "The store catalogue the launcher works from."),
                (Path.Combine(DataDirectory, "SDMeta"),
                    "Store metadata the launcher reads rather than re-fetches."),
                (Path.Combine(DataDirectory, "ThirPartyManagedApps"),
                    "Which installed applications another store manages. The spelling is Epic's."),
                (Path.Combine(DataDirectory, "Launcher.manifest"),
                    "The launcher's record of the build it is running."),
                (Path.Combine(DataDirectory, "Launcher.manifest.meta"),
                    "The metadata beside that record."),
                (Path.Combine("UnrealEngineLauncher", "LauncherInstalled.dat"),
                    "The machine's record of where its Epic games are installed, which other "
                    + "launchers read to find them."),
                ("EpicOnlineServices",
                    "The services Epic games sign in and play online through."),
            ]),
    ];

    /// <summary>
    /// Every path this provider could ever target, by declaration rather than by enumeration — so
    /// answering "is there anything here?" costs one existence check and can never reach anything
    /// the table does not name.
    /// </summary>
    private IEnumerable<string> DeclaredPaths() =>
        from root in _roots
        from location in root.Locations
        select Path.Combine(root.Path, location.RelativePath);
}
