using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The streaming cache the Spotify desktop app keeps so that a song or podcast it has played plays
/// again without lagging.
///
/// <para><b>Spotify keeps two stores beside each other, and only one of them is a cache.</b>
/// <c>Data</c> is the streaming cache. <c>Storage</c> is the music and podcasts the user downloaded
/// to play offline, which is a Premium feature: getting them back needs an active subscription, and
/// Deguffer cannot see whether there is one. A user whose Premium has lapsed would lose offline
/// listening for good, so the downloads are never offered at any tier. At least two general-purpose
/// cleaners treat <c>Storage</c> as cache, which is the kind of misclassification §3 describes.</para>
///
/// <para><b>Where the downloads are is read, never assumed.</b> Spotify's Settings page moves its
/// storage and records the move in its settings file, and it is not possible to tell which store
/// moved. <see cref="SpotifyStorage"/> carries that argument. Here it means nothing in a moved
/// location is measured or removed, the location is asserted to survive, and a cache is not offered
/// at all where its downloads may be in it.</para>
///
/// <para><b>§5.1 has a route, and Deguffer cannot take it.</b> Spotify documents clearing the cache
/// from inside the running app, under Settings, Storage, Clear cache, and ships nothing that does the
/// same from outside. So the cache is deleted by path, and it is named outright rather than found:
/// no Spotify folder is ever enumerated, so nothing unnamed beside the cache can be reached.</para>
/// </summary>
public sealed class SpotifyCacheProvider : CleanupProviderBase
{
    private const string CacheReason =
        "Parts of songs and podcasts Spotify streamed, kept so they play again without lagging. "
        + "Spotify streams them again when they are next played.";

    private const string WithheldCacheReason =
        "Spotify's streaming cache, left alone because the music and podcasts you downloaded may be "
        + "in it.";

    private const string OfflineStoreReason =
        "The music and podcasts you downloaded to play offline. Getting them back needs an active "
        + "Premium subscription.";

    private const string AccountsReason =
        "Spotify's settings and saved state for each account signed in on this computer.";

    private const string CacheFolderReason =
        "This is Spotify's own folder. Deguffer removes the streaming cache inside it and nothing "
        + "else, and never the music and podcasts you downloaded.";

    private const string SettingsFolderReason =
        "This is where Spotify keeps its settings and who is signed in. Deguffer removes nothing here.";

    private const string LocationReason =
        "Where Spotify's settings say it keeps its storage. Deguffer never measures or removes "
        + "anything in it, because the music and podcasts you downloaded may be there.";

    private IReadOnlyList<DeclaredRoot>? _roots;
    private IReadOnlyList<ToolRoot>? _toolRoots;
    private SpotifyStorage? _storage;

    public SpotifyCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
    }

    public override string Id => "spotify";

    public override string Name => "Spotify streaming cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Spotify streams songs and podcasts it had kept on disk again the next time they play, so "
        + "they may take a moment to start and use more data for a while. The music and podcasts you "
        + "downloaded, your settings and your sign-in are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Spotify desktop app",
        Publisher = "Spotify",
        Purpose = "Spotify keeps parts of the music and podcasts it streams on disk, so they can play "
            + "without lagging. That cache is separate from the music and podcasts you download to "
            + "listen to offline, which Spotify keeps in a folder of its own.",
        Recommendation = "Deguffer removes the streaming cache by name and nothing else. It never "
            + "removes your downloads, because getting them back needs an active Premium "
            + "subscription, and it reads Spotify's settings to find where they are rather than "
            + "assuming.",
    };

    /// <summary>
    /// What this provider names, edition by edition. Exposed so tests can assert that no Spotify
    /// folder is a target and that a withheld cache is not declared.
    /// </summary>
    public IReadOnlyList<DeclaredRoot> Roots => _roots ??= Declare();

    /// <summary>§5.3. The app holds its cache open while it plays.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["Spotify"];

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside: each edition's cache folder and settings folder, the
    /// Store edition's package folder, and every moved storage location.
    ///
    /// <para>A withheld cache is refused here too. Otherwise Explore would allow the one directory
    /// the Storage page has just declined, for the reason it declined it.</para>
    ///
    /// <para>A moved location is refused wherever it is, including a folder above Spotify's own, so
    /// everything in it that no deeper declaration recognises is refused as well. That is the
    /// direction §5.2 requires, and the price falls only on a machine whose storage was pointed at a
    /// folder that general.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots => _toolRoots ??= DeclareToolRoots();

    /// <summary>
    /// The settings files are read once per planning pass (G4). Presence, planning and the §5.2
    /// declarations all ask the same question of them.
    /// </summary>
    private SpotifyStorage Storage => _storage ??= SpotifyStorage.Find(Environment);

    public override void InvalidateCaches()
    {
        _storage = null;
        _roots = null;
        _toolRoots = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// Presence is a cache folder on disk, or a sentence owed about where Spotify's storage is.
    ///
    /// <para>The second half is there because the planner never asks an absent provider for a plan.
    /// A settings file that could not be read, or a moved location, would otherwise leave the row
    /// reading "Not installed" about a Spotify that is installed.</para>
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var storage = Storage;

        return Task.FromResult(
            storage.Installs.Any(install => LongPath.DirectoryMayExist(install.Edition.Cache))
            || storage.Unsettled is not null
            || storage.Moved.Count > 0);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var storage = Storage;
        var scan = DeclaredLocations.Examine(Roots, ct);

        // A cache Windows would not describe is withheld all the same. What the sentence below says
        // about it is a fact about Spotify's settings, not about the folder, and dropping it would
        // leave a moved location's overlap unexplained.
        var withheld = storage.Installs
            .Where(install => !storage.MayOffer(install.Edition))
            .Select(install => (install.Edition, Presence: LongPath.ProbeDirectory(install.Edition.Cache)))
            .Where(withholding => withholding.Presence is not PathPresence.Absent)
            .ToList();

        var unreached = scan.CouldNotBeReached;

        var owesASentence = withheld.Count > 0 || storage.Unsettled is not null || storage.Moved.Count > 0;

        if (scan.FoundNothing && !owesASentence)
        {
            return EmptyPlan("Spotify is keeping no streaming cache on this machine.");
        }

        var notes = new List<PlanNote>(scan.Notes);
        var survivors = new List<(string Path, string Reason)>(scan.Protected);
        var explained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (storage.Unsettled is { } unsettled)
        {
            notes.Add(Information(UnsettledSentence(unsettled, withheld.Count > 0)));
        }

        foreach (var (edition, presence) in withheld)
        {
            var cache = edition.Cache;

            if (presence is PathPresence.Present)
            {
                survivors.Add((cache, WithheldCacheReason));
            }
            else
            {
                // Not a survivor: §5.6 cannot measure it, and would pass whatever happened to it. Named
                // unless the folder holding it already was, which says the same thing about it.
                if (!scan.Unreachable.Any(root => LongPath.Contains(root, cache)))
                {
                    notes.Add(UnreadableRoot.UnreachedNote(cache));
                }

                unreached = true;
            }

            foreach (var location in storage.Overlapping(edition))
            {
                explained.Add(location);
                // "Keeps, or kept": a location comes from either key, and storage.last-location is
                // where the storage was before, so neither tense alone is true of both.
                notes.Add(Information(
                    (location.Equals(cache, StringComparison.OrdinalIgnoreCase)
                        ? $"Spotify's settings name its cache folder, '{cache}', as where it keeps, or kept, its storage."
                        : $"Spotify's settings name '{location}' as where it keeps, or kept, its storage, and that "
                            + $"overlaps its cache in '{cache}'.")
                    + " The music and podcasts you downloaded may be in there, so Deguffer left the cache alone."));
            }
        }

        // Every other moved location, including one that holds a cache folder not on disk, is named
        // in a sentence of its own. Nothing else would tell the user it was never examined.
        notes.AddRange(storage.Moved.Where(location => !explained.Contains(location)).Select(location => Information(
            $"Spotify's settings name '{location}' as where it keeps, or kept, its storage. The music and "
            + "podcasts you downloaded may be there, and Spotify's own help calls that folder its cache, so "
            + "Deguffer did not measure or remove anything in it. Whatever is there was neither cleared nor "
            + "ruled out.")));

        survivors.AddRange(storage.Locations.Select(location => (location, LocationReason)));

        foreach (var install in storage.Installs.Where(install => install.IsInstalled))
        {
            survivors.AddRange(Survivors(install.Edition));
        }

        var (steps, measured) = await PlanDeletionsAsync(scan.Targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        // §5.3, and only where something is going to be removed. A warning that Spotify holds files
        // open, on a row with nothing to delete, describes a clean that will not happen.
        if (scan.Targets.Count > 0 && BuildRunningProcessNote() is { } warning)
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
            ProtectedPaths = Protect([.. survivors.DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase)]),
            Notes = notes,
            Fallback = measured.Fallback,

            // A withheld cache and a moved location count here for the reason a declined link does:
            // something was never examined, so the row must not read clear.
            WasNotExamined = scan.Targets.Count == 0 && (scan.Declined.Count > 0 || owesASentence),
            HasUnreadableRoot = unreached,
        };
    }

    private static PlanNote Information(string message) => new(PlanNoteSeverity.Information, message);

    private static string UnsettledSentence(SpotifySettings settings, bool withheldACache) =>
        (settings.Reading == SpotifySettingsReading.Unreadable
            ? $"Deguffer could not read Spotify's settings in '{settings.File}'"
            : $"Deguffer could not make sense of Spotify's settings in '{settings.File}'")
        + ", so it cannot tell where Spotify keeps the music and podcasts you downloaded."
        + (withheldACache
            ? " Deguffer left Spotify's streaming cache alone in case they are in it."
            : " Anything Spotify keeps somewhere else was neither cleared nor ruled out.");

    /// <summary>
    /// What §5.6 asserts survived for one edition. The downloads, their index and the settings file
    /// are named outright, because an assertion that the folder above them survived would pass with
    /// each of them gone. <c>offline.bnk</c> is a file, and a file is only ever asserted where a
    /// provider names it.
    /// </summary>
    private static IEnumerable<(string Path, string Reason)> Survivors(SpotifyEdition edition) =>
    [
        (edition.CacheFolder, "Spotify's own folder must survive. Only the streaming cache inside it is removed."),
        (edition.OfflineStore, OfflineStoreReason),
        (edition.OfflineIndex, "Spotify's record of the music and podcasts you downloaded."),
        (edition.SettingsFolder, "Spotify's settings folder must survive. Nothing in it is a cache."),
        (edition.SettingsFile, "Spotify's settings, including where it keeps your downloads and who is signed in."),
        (edition.Accounts, AccountsReason),
    ];

    private IReadOnlyList<DeclaredRoot> Declare()
    {
        var storage = Storage;

        return
        [
            .. storage.Installs.Select(install => new DeclaredRoot(
                install.Edition.Root,
                install.Edition.Package is null
                    ? CacheFolderReason
                    : "This is the folder Windows gives the Microsoft Store edition of Spotify. Deguffer "
                        + "removes the streaming cache inside it and nothing else.",
                RequiresElevation: false,
                storage.MayOffer(install.Edition)
                    ? [new DeclaredLocation(Path.GetRelativePath(install.Edition.Root, install.Edition.Cache), CacheReason)]
                    : [],
                [])),
        ];
    }

    /// <summary>
    /// A <see cref="ToolRoot"/> classifies a directory's immediate children, so each folder that
    /// holds something named is declared on its own. The downloads are declared in whichever folder
    /// holds them, which is the cache folder for one edition and the settings folder for the other.
    /// </summary>
    private IReadOnlyList<ToolRoot> DeclareToolRoots()
    {
        var storage = Storage;
        var roots = new List<ToolRoot>();

        foreach (var edition in storage.Installs.Select(install => install.Edition))
        {
            var offered = storage.MayOffer(edition);

            if (edition.Package is { } package)
            {
                roots.Add(ToolRoot.Of(
                    package,
                    "This is the folder Windows gives the Microsoft Store edition of Spotify. Deguffer "
                    + "removes the streaming cache from inside it, from the Storage page, and nothing "
                    + "else.",
                    new DisposableChildSet([])));
            }

            roots.Add(ToolRoot.Of(
                edition.CacheFolder,
                CacheFolderReason,
                Children(
                    edition,
                    edition.CacheFolder,
                    new ChildClassification(
                        SpotifyEdition.CacheName,
                        offered ? SafetyTier.RegenerableCache : SafetyTier.DoNotTouch,
                        offered ? CacheReason : WithheldCacheReason))));

            roots.Add(ToolRoot.Of(
                edition.SettingsFolder,
                SettingsFolderReason,
                Children(
                    edition,
                    edition.SettingsFolder,
                    new ChildClassification(SpotifyEdition.AccountsName, SafetyTier.DoNotTouch, AccountsReason))));
        }

        // A volume root is left out. Refusing every child of a drive would take the whole drive away
        // from Explore for one Spotify setting, and a volume root is never itself removable there.
        roots.AddRange(
            from location in storage.Moved
            where VolumeRoot.Below(location) is not null
            select ToolRoot.Of(location, LocationReason, new DisposableChildSet([])));

        return roots;
    }

    private static DisposableChildSet Children(SpotifyEdition edition, string folder, ChildClassification own) =>
        new(edition.OfflineStoreParent.Equals(folder, StringComparison.OrdinalIgnoreCase)
            ? [own, new ChildClassification(SpotifyEdition.OfflineStoreName, SafetyTier.DoNotTouch, OfflineStoreReason)]
            : [own]);
}
