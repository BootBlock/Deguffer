using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Firefox's own caches, which nothing in <see cref="ChromiumCacheProvider"/> reaches (1.9 GB in
/// the local half of one profile on the measured machine, 350 MB of it in the five directories
/// offered here).
///
/// <para><b>Firefox splits a profile across two roots, and only one of them is ever touched.</b>
/// The roaming half under <c>%APPDATA%\Mozilla\Firefox\Profiles\&lt;profile&gt;</c> holds
/// <c>places.sqlite</c>, <c>key4.db</c>, <c>logins.json</c>, <c>cert9.db</c> and <c>prefs.js</c> —
/// bookmarks, history, saved passwords and every preference. The local half under
/// <c>%LOCALAPPDATA%</c> holds the disk cache and the other regenerable files. This provider plans
/// against the local half alone, and asserts the roaming half survived (§5.6).</para>
///
/// <para><b>Identification is positive, and it is <c>profiles.ini</c>.</b> A directory is a Firefox
/// profile because Mozilla's own register names it, never because it is named like one or holds a
/// directory called <c>cache2</c>. <see cref="MozillaProfileDiscovery"/> answers that question, and
/// this provider is only ever asked what may go inside a directory that has already passed it — the
/// same separation, for the same reason, as the Chromium provider's.</para>
///
/// <para><b>The local path is derived rather than enumerated, so every segment of it is checked for
/// a link.</b> Elsewhere in Deguffer a target arrives from an enumeration that has already filtered
/// links out, and one reparse check on the directory itself completes the argument. Nothing
/// enumerates here: the whole path from <c>%LOCALAPPDATA%</c> to the profile is built from a text
/// file plus two constants, and relocating <c>%LOCALAPPDATA%\Mozilla</c> onto another volume with a
/// junction is a thing people do. A link anywhere along it would put the deletion on the far side
/// while every §5.6 survivor named below resolved through the same link and passed — the vacuous
/// negative. <see cref="FirstLinkBetween"/> is what stops that.</para>
///
/// <para>§5.1 does not apply. Firefox clears its cache only from inside the running browser, and
/// Mozilla's own published advice for reclaiming the space outside it is to delete <c>cache2</c>.
/// </para>
///
/// <para>Thunderbird keeps the identical layout, so it is a second provider over the same discovery
/// rather than a change to this one. It is not shipped: nothing here has been measured against it.
/// </para>
/// </summary>
public sealed class FirefoxCacheProvider : CleanupProviderBase
{
    /// <summary>Firefox's directory under each application-data root.</summary>
    private const string ApplicationDirectory = @"Mozilla\Firefox";

    /// <summary>
    /// The synchronised dataset that dominates the local profile and is deliberately not offered.
    /// See <see cref="Children"/> for the reasoning, and <see cref="BuildPlanAsync"/> for the
    /// sentence the user is shown about it.
    /// </summary>
    private const string SynchronisedDataName = "remote-settings";

    /// <summary>
    /// What may be removed from a profile's <em>local</em> half. Anything not named here is Tier 4
    /// by construction, which is the direction §5.2 requires the unknown case to fail in.
    ///
    /// <para>Every one of the five is content Firefox refills without being asked, and every one of
    /// them sits in the half of the profile that holds no user data. The names that make the
    /// allow-list load-bearing are in the <em>other</em> half and are never enumerated at all, so
    /// they are asserted by name instead — see <see cref="ProtectedRoamingFiles"/>.</para>
    ///
    /// <para><c>remote-settings</c> is declared at Tier 4 rather than omitted, on the reasoning
    /// Chromium's <c>Cache</c> and <c>Service Worker</c> are: leaving it out would classify it with
    /// the generic "not recognised" sentence, when in fact it is recognised, it is four fifths of
    /// the directory, and there is a specific reason it is not on offer. Firefox synchronises it for
    /// itself — most of it the Firefox Suggest dataset — so it is re-downloadable rather than
    /// user-authored, but Mozilla documents no way to remove it and what a re-sync costs was never
    /// established. It is measured and reported instead.</para>
    /// </summary>
    public static readonly DisposableChildSet Children = new(
    [
        new ChildClassification(
            "cache2",
            SafetyTier.RegenerableCache,
            "Web content Firefox saved so it would not fetch the same thing twice. It is downloaded again when it is next wanted."),
        new ChildClassification(
            "startupCache",
            SafetyTier.RegenerableCache,
            "Precompiled interface and script data Firefox builds for itself. It rebuilds it on the next start, so one start is slower."),
        new ChildClassification(
            "safebrowsing",
            SafetyTier.RegenerableCache,
            "The downloaded Safe Browsing lists Firefox checks sites against. Firefox fetches them again shortly after it next starts."),
        new ChildClassification(
            "thumbnails",
            SafetyTier.RegenerableCache,
            "Page images for the new-tab page. Firefox draws each one again the next time you visit the page."),
        new ChildClassification(
            "jumpListCache",
            SafetyTier.RegenerableCache,
            "Icons for Firefox's taskbar jump list. Firefox rebuilds them on demand."),
        new ChildClassification(
            SynchronisedDataName,
            SafetyTier.DoNotTouch,
            "Datasets Firefox synchronises for itself, most of it the Firefox Suggest data. It is not "
            + "a cache Mozilla documents removing, and what re-downloading it would cost is not "
            + "established, so Deguffer measures it and leaves it alone."),
    ]);

    /// <summary>
    /// Nothing is disposable in the roaming half. It is a <see cref="ToolRoot"/> so that §7.1's
    /// second deletion route refuses it too: Explore draws every directory on the drive and lets the
    /// user act on one, and "never the roaming profile at all" is not a rule that can hold on the
    /// Storage page alone.
    /// </summary>
    private static readonly DisposableChildSet NothingIsDisposable = new([]);

    /// <summary>
    /// The user data in the roaming half, named in full rather than sampled.
    ///
    /// <para>Named at all for the reason NVIDIA's <c>accounts</c> and Chromium's <c>Login Data</c>
    /// are: child classification enumerates directories, so a file is never seen, never classified
    /// and never asserted unless the provider names it. These five are the entire reason the roaming
    /// half is out of bounds, and an assertion that the profile directory survived would pass with
    /// every one of them gone.</para>
    /// </summary>
    private static readonly (string Name, string Reason)[] ProtectedRoamingFiles =
    [
        ("places.sqlite", "Your bookmarks and browsing history."),
        ("key4.db", "The key that decrypts your saved passwords."),
        ("logins.json", "Your saved usernames and passwords."),
        ("cert9.db", "The certificates and exceptions you have accepted."),
        ("prefs.js", "Every setting you have changed in Firefox."),
    ];

    private static readonly char[] Separators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private const string LinkReason =
        "A link rather than a directory, so what it points at was never classified.";

    private readonly MozillaProfileDiscovery _discovery;
    private IReadOnlyList<MozillaProfile>? _profiles;
    private IReadOnlyList<ToolRoot>? _toolRoots;

    public FirefoxCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
        => _discovery = new MozillaProfileDiscovery(Environment, ApplicationDirectory);

    public override string Id => "firefox";

    public override string Name => "Firefox caches";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "Firefox fetches pages from the network instead of from disk for a while, and rebuilds its " +
        "startup cache the first time it opens, so one start is slower. Bookmarks, history, saved " +
        "passwords, open tabs and settings are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Mozilla Firefox",
        Publisher = "Mozilla",
        Purpose = "Firefox keeps a profile in two places. Your bookmarks, history, saved passwords "
            + "and settings live under your roaming profile. The disk cache, the startup cache and "
            + "the downloaded Safe Browsing lists live separately under your local profile, and "
            + "Firefox refills every one of them by itself.",
        Recommendation = "Deguffer removes five named cache directories from the local half of each "
            + "profile, and never removes anything at all from the half your bookmarks and "
            + "passwords are in.",
    };

    /// <summary>§5.3. Firefox holds its own cache open while it runs.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["firefox"];

    /// <summary>
    /// The profiles <c>profiles.ini</c> names, memoised for the life of a planning pass (G4).
    /// Presence, planning and <see cref="ToolRoots"/> all ask the same question of the same file.
    ///
    /// Exposed so tests can assert that no roaming profile is ever a target.
    /// </summary>
    public IReadOnlyList<MozillaProfile> Profiles(CancellationToken ct = default) =>
        _profiles ??= _discovery.Discover(ct);

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside: Firefox's own folder under each application-data
    /// root, then both halves of every profile — the local half declaring the five caches, and
    /// everything else declaring nothing at all.
    ///
    /// <para>The roaming side is declared even though this provider never plans against it, and that
    /// is the point. Explore's refusals come from these declarations, so without them a user could
    /// delete <c>logins.json</c> from the size picture while the Storage page was carefully leaving
    /// it alone.</para>
    ///
    /// <para><b>The folders above a profile are declared too, and refusing the profile alone is
    /// worth nothing without them.</b> <c>%APPDATA%\Mozilla\Firefox</c> and the <c>Profiles</c>
    /// directory under it hold every profile's password database between them, so a refusal that
    /// stopped at the profile would refuse a directory and permit its parent. They are declared
    /// unconditionally rather than from what discovery found, because a register that would not be
    /// read leaves no profiles and the files are on disk regardless — and a declaration naming a
    /// directory that is not there refuses nothing.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
        _toolRoots ??=
        [
            ToolRoot.Of(
                _discovery.RoamingRoot,
                "This is Firefox's own folder. Your profiles are inside it, with your bookmarks, "
                + "history and saved passwords, and Deguffer removes nothing from any of them.",
                NothingIsDisposable),
            ToolRoot.Of(
                _discovery.LocalRoot,
                "This is where Firefox keeps the caches for each of your profiles. Deguffer removes "
                + "them from the Storage page, where it knows which of them are caches.",
                NothingIsDisposable),
            .. from profile in Profiles()
               from root in new[]
               {
                   ToolRoot.Of(
                       profile.LocalPath,
                       $"This is the cache folder for your '{profile.Name}' Firefox profile. Deguffer "
                       + "removes the caches inside it from the Storage page, where it knows which of "
                       + "them are caches.",
                       Children),
                   ToolRoot.Of(
                       profile.RoamingPath,
                       $"This is your '{profile.Name}' Firefox profile — bookmarks, history, saved "
                       + "passwords and settings. Nothing in here is a cache, and Deguffer removes "
                       + "nothing from it.",
                       NothingIsDisposable),
               }
               select root,
        ];

    public override void InvalidateCaches()
    {
        _profiles = null;
        _toolRoots = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// Presence is a cache actually on disk, never a profile existing. A profile that has been
    /// opened once and closed keeps a local half with nothing disposable in it, and reporting that
    /// as a source would offer the user a row the plan then has nothing to say about.
    ///
    /// <para>A <c>profiles.ini</c> that would not be read counts as present, for the reason the
    /// Chromium provider's refused application-data root does: the planner never asks an absent
    /// provider for a plan, so the row would read "Not installed" about a file Deguffer never read.
    /// Answering true sends the pass into <see cref="BuildPlanAsync"/>, which says so.</para>
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var profiles = Profiles(ct);

        return Task.FromResult(
            _discovery.ProfilesUnreadable || profiles.Any(profile => HasRecognisedCache(profile, ct)));
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var profiles = Profiles(ct);

        if (profiles.Count == 0)
        {
            if (_discovery.ProfilesUnreadable)
            {
                return EmptyPlan(
                    $"Deguffer could not read '{_discovery.ProfilesPath}', so it could not work out "
                    + "which Firefox profiles exist. Nothing was planned, and nothing was ruled out "
                    + "either.") with { HasUnreadableRoot = true };
            }

            // Every profile there is sits outside Firefox's own folder. Saying "no profile" here
            // would be the same untruth as saying it about a register nobody could read.
            return _discovery.ProfilesElsewhere.Count > 0
                ? UnexaminedPlan(StoredElsewhere(_discovery.ProfilesElsewhere))
                : EmptyPlan("Firefox has no profile in this user's account.");
        }

        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();
        var declined = new List<(string Path, string Reason)>();
        var survivors = new List<(string Path, string Reason)>();
        var unreadable = false;

        survivors.Add((
            _discovery.RoamingRoot,
            "Firefox's own folder, which holds the register of your profiles and the profiles "
            + "themselves. Nothing is ever removed from it."));
        survivors.Add((
            _discovery.ProfilesPath,
            "Firefox's register of your profiles. Removing it would lose every profile."));
        survivors.Add((
            _discovery.LocalRoot,
            "Firefox's cache folder itself must survive — only recognised caches inside a profile "
            + "under it are removed."));

        if (_discovery.ProfilesElsewhere.Count > 0)
        {
            // Not "we left them alone": nothing about them was established at all, and a plan that
            // implied otherwise would be claiming a folder is clear that was never looked at.
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                StoredElsewhere(_discovery.ProfilesElsewhere)));
        }

        foreach (var profile in profiles)
        {
            ct.ThrowIfCancellationRequested();

            survivors.Add((
                profile.RoamingPath,
                $"Your '{profile.Name}' Firefox profile. Deguffer removes nothing from it."));
            survivors.AddRange(ProtectedRoamingFiles.Select(
                file => (Path.Combine(profile.RoamingPath, file.Name), file.Reason)));

            // Before the existence check, not after it. A link partway up the path resolves onto a
            // directory that holds no profile of its own, so the profile itself reads as absent and
            // the pass would end reporting nothing at all about a redirection it did detect.
            if (FirstLinkBetween(Environment.LocalAppData, profile.LocalPath) is { } link)
            {
                notes.Add(LinkNote(link));
                declined.Add((link, LinkReason));
                continue;
            }

            if (!LongPath.DirectoryExists(profile.LocalPath))
            {
                continue;
            }

            survivors.Add((
                profile.LocalPath,
                $"The cache folder for your '{profile.Name}' profile must survive — only recognised "
                + "caches inside it are removed."));

            var scan = ChildDirectories.Under(profile.LocalPath);

            if (scan.Unreadable)
            {
                notes.Add(UnreadableRoot.Note(profile.LocalPath));
                unreadable = true;
                continue;
            }

            foreach (var linked in scan.Links)
            {
                var path = LongPath.Display(linked.FullName);
                notes.Add(LinkNote(path));
                declined.Add((path, LinkReason));
            }

            var spared = 0;

            foreach (var child in scan.Directories)
            {
                var classification = Children.Classify(child.Name);
                var path = LongPath.Display(child.FullName);

                if (classification.Tier.IsOfferable())
                {
                    targets.Add(new DeletionTarget(path, classification.Reason));
                    continue;
                }

                survivors.Add((path, classification.Reason));
                spared++;

                if (child.Name.Equals(SynchronisedDataName, StringComparison.OrdinalIgnoreCase))
                {
                    // The issue this provider answers found this one directory to be four fifths of
                    // the profile, so a plan that only said "left alone" would leave the user unable
                    // to account for the space. Measured unguarded: nothing is being deleted, so
                    // withholding recent files would describe a deletion that is not happening.
                    var size = await Scanner
                        .MeasureAsync(path, MinimumAge.Off, progress: null, ct)
                        .ConfigureAwait(false);

                    notes.Add(new PlanNote(
                        PlanNoteSeverity.Information,
                        $"Firefox keeps {FreeSpace.Format(size.Size)} of synchronised data in "
                        + $"'{path}', most of it the Firefox Suggest dataset. It is left alone: "
                        + "Mozilla documents no way to remove it, and what re-downloading it would "
                        + "cost has not been established."));
                }
            }

            // One note per profile rather than one per spared child. Each is still asserted
            // individually by §5.6, and this is the sentence that says so.
            if (spared > 0)
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"In the '{profile.Name}' profile, {spared} other "
                    + $"{(spared == 1 ? "item is" : "items are")} left alone beside the caches."));
            }
        }

        // Said as a note rather than as an early return, because the pass has other things to
        // report even when it found no cache: the size of the synchronised data it deliberately did
        // not offer, and any profile stored somewhere it will not examine. Returning an empty plan
        // here discarded both, so a profile holding 1.5 GB of remote-settings and nothing else read
        // as clear with no account of the space.
        if (targets.Count == 0 && declined.Count == 0 && !unreadable)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "No Firefox profile on this machine is keeping a cache on disk."));
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        // §5.3, and only where something is actually going to be removed. A warning that Firefox is
        // holding files open, on a row with nothing to delete, describes a clean that will not
        // happen.
        if (targets.Count > 0 && BuildRunningProcessNote() is { } warning)
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
            ProtectedPaths = Protect(
                [.. survivors.Concat(declined).DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase)]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = unreadable,
            // A profile kept outside Firefox's folder counts here for the same reason a declined
            // link does: nothing was removed and something was never looked at, so the shell must
            // not call the row clear.
            WasNotExamined = targets.Count == 0
                && (declined.Count > 0 || _discovery.ProfilesElsewhere.Count > 0),
        };
    }

    /// <summary>
    /// The first directory between <paramref name="baseDirectory"/> and <paramref name="target"/>,
    /// inclusive of the target, that is a link rather than a directory — or null when none of them
    /// is.
    ///
    /// <para>Every segment, not just the last, because every segment of this path was synthesised.
    /// <paramref name="target"/> is <c>%LOCALAPPDATA%</c> plus <c>Mozilla\Firefox</c> plus a
    /// relative path read out of a text file, so nothing on the way down has been seen by an
    /// enumeration that filters links. A junction at <c>Mozilla</c> is as effective at redirecting
    /// the deletion as one at the profile itself, and rather more likely: relocating a browser cache
    /// onto another volume is a thing people do deliberately.</para>
    /// </summary>
    private static string? FirstLinkBetween(string baseDirectory, string target)
    {
        var walked = baseDirectory;

        foreach (var segment in target[baseDirectory.Length..]
                     .Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            walked = Path.Combine(walked, segment);

            if (LongPath.IsReparsePoint(walked))
            {
                return walked;
            }
        }

        return null;
    }

    /// <summary>
    /// The sentence for a profile <c>profiles.ini</c> puts outside Firefox's own folder. Said in
    /// two places, because such a profile can be one of several or the only one there is, and the
    /// second case returns before the plan is ever built.
    /// </summary>
    private static string StoredElsewhere(IReadOnlyList<string> names) =>
        $"The '{string.Join("' and '", names)}' {(names.Count == 1 ? "profile is" : "profiles are")} "
        + "kept outside Firefox's own folder. Deguffer did not examine "
        + $"{(names.Count == 1 ? "it" : "them")}: a profile stored that way keeps its cache among "
        + "your bookmarks and passwords rather than separately from them.";

    private static PlanNote LinkNote(string path) => new(
        PlanNoteSeverity.Information,
        $"Leaving '{path}' alone: it is a link to somewhere else, and Deguffer does not delete "
        + "through a link.");

    /// <summary>
    /// Whether any of the five offerable names is on disk for this profile, by probing the table
    /// rather than by enumerating (G4). Five existence checks per profile, and not one of them can
    /// reach a path the table does not name.
    ///
    /// <para><b>A presence probe, not a safety gate.</b> It answers through a junction, so a profile
    /// whose only cache is a link reports as present here and then yields no target, because
    /// <see cref="BuildPlanAsync"/> declines it. A future edit must not read a true from this as
    /// licence to delete anything.</para>
    /// </summary>
    private static bool HasRecognisedCache(MozillaProfile profile, CancellationToken ct)
    {
        foreach (var name in Children.DisposableNames)
        {
            ct.ThrowIfCancellationRequested();

            if (LongPath.DirectoryExists(Path.Combine(profile.LocalPath, name)))
            {
                return true;
            }
        }

        return false;
    }
}
