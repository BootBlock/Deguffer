using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Chromium caches inside desktop applications that embed the engine (~0.8 GB across ten
/// applications on the audited machine, and a published 2 to 5 GB for one heavily used chat client).
///
/// <para>Cleaners handle browsers. Almost none handle the applications that ship Chromium inside
/// themselves, each writing the same fixed set of cache directory names under its own vendor name.
/// That is what makes this recognisable by shape rather than by name: the directory names belong to
/// Chromium, not to the vendor, so one provider reaches an unbounded set of applications without
/// knowing any of them.</para>
///
/// <para><b>The signature is an exact allow-list of six names, and that is the whole safety
/// argument.</b> What sits beside them is Tier 3 and looks identical: <c>Local Storage</c>,
/// <c>Session Storage</c> and <c>IndexedDB</c> are directories in the same folder in the same
/// naming style, and <c>Local State</c>, <c>Cookies</c> and <c>Login Data</c> are files among them.
/// Between them they hold sign-in tokens, saved passwords, drafts and offline application data. So
/// this is §5.2 applied to a signature instead of to a root — a name the table does not carry is
/// Tier 4 by construction, and everything spared that is actually on disk is asserted to survive.
/// </para>
///
/// <para><b>A cache name is not on its own a licence to look inside a folder.</b> Any directory
/// anywhere may be called <c>GPUCache</c>, so identification is a separate and positive judgement:
/// <see cref="ChromiumUserDataDiscovery"/> requires the folder to hold Chromium's own
/// <c>Local State</c> file before this provider is ever asked what may go inside it. The six names
/// then say what may be deleted; they never say whose folder this is.</para>
///
/// <para>§5.1 does not apply. No embedding application exposes a cache-eviction command, and the
/// engine's own clear-browsing-data surface is reachable only from inside the running process.</para>
///
/// <para>Packaged (MSIX) applications are out of reach here, deliberately. Windows redirects their
/// <c>%APPDATA%</c> to <c>%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Roaming</c>, and
/// classifying that redirection is its own piece of work — see §3 of
/// <c>docs/todo/unreached-locations.md</c>. Scanning one level under the two application-data roots
/// also leaves the browsers themselves out, which is intended: Chrome and Edge keep their user data
/// three levels down, and every general-purpose cleaner already reaches them.</para>
/// </summary>
public sealed class ChromiumCacheProvider : CleanupProviderBase
{
    /// <summary>
    /// Chromium's six cache directories, grouped by the directory each sits in. Anything not named
    /// here is Tier 4 by construction — which is what makes "we did not recognise that" fail closed
    /// beside data that would be gone for good.
    ///
    /// <para><c>Cache</c> and <c>Service Worker</c> appear as Tier 4 entries rather than as
    /// omissions, because they are the one case where the unrecognised-child reason would be
    /// actively misleading: the directory really is left standing, and something inside it really is
    /// being removed. Declaring them says both.</para>
    /// </summary>
    public static readonly IReadOnlyList<ChromiumCacheLevel> Levels =
    [
        new ChromiumCacheLevel(string.Empty, new DisposableChildSet(
        [
            new ChildClassification(
                "Code Cache",
                SafetyTier.RegenerableCache,
                "Compiled JavaScript and WebAssembly. The application recompiles each script the first time it runs again."),
            new ChildClassification(
                "GPUCache",
                SafetyTier.RegenerableCache,
                "Compiled graphics pipelines. The application rebuilds them on demand."),
            new ChildClassification(
                "DawnGraphiteCache",
                SafetyTier.RegenerableCache,
                "Compiled WebGPU pipelines. The application rebuilds them on demand."),
            new ChildClassification(
                "DawnWebGPUCache",
                SafetyTier.RegenerableCache,
                "Compiled WebGPU pipelines. The application rebuilds them on demand."),
            new ChildClassification(
                "Cache",
                SafetyTier.DoNotTouch,
                "The web cache directory. Its index sits beside the data, so only the 'Cache_Data' inside it is removed."),
            new ChildClassification(
                "Service Worker",
                SafetyTier.DoNotTouch,
                "Service-worker registrations and scripts, next to the responses they cached. Only the 'CacheStorage' inside it is removed."),
        ])),
        new ChromiumCacheLevel("Cache", new DisposableChildSet(
        [
            new ChildClassification(
                "Cache_Data",
                SafetyTier.RegenerableCache,
                "Web content the application saved so it would not fetch the same thing twice. It is downloaded again when it is next wanted."),
        ])),
        new ChromiumCacheLevel("Service Worker", new DisposableChildSet(
        [
            new ChildClassification(
                "CacheStorage",
                SafetyTier.RegenerableCache,
                "Responses a service worker stored for offline use. It fetches them again the next time the application is online."),
        ])),
    ];

    /// <summary>
    /// Files in the user-data folder that §5.6 must assert survived. Named separately because a
    /// <see cref="DisposableChildSet"/> only ever classifies a directory, so a file beside the
    /// caches is never enumerated, never classified, and never asserted unless it is named here —
    /// the lesson NVIDIA's <c>accounts</c> taught, in a folder with a great deal more to lose.
    /// </summary>
    private static readonly (string Name, string Reason)[] ProtectedRootFiles =
    [
        (ChromiumUserDataDiscovery.IdentifyingFile,
            "The application's own settings, and the key that decrypts its saved cookies and passwords."),
    ];

    /// <summary>
    /// The same, per profile. <c>Cookies</c> is listed at both paths because Chromium moved it under
    /// <c>Network</c> and older profiles still keep it at the top level. Whichever is absent records
    /// itself as nothing to preserve rather than as a pass.
    /// </summary>
    private static readonly (string Name, string Reason)[] ProtectedProfileFiles =
    [
        ("Cookies", "Sign-in cookies. Removing them signs the user out of everything."),
        (@"Network\Cookies", "Sign-in cookies. Removing them signs the user out of everything."),
        ("Login Data", "Saved usernames and passwords."),
    ];

    private readonly ChromiumUserDataDiscovery _discovery;
    private IReadOnlyList<ChromiumUserData>? _applications;

    public ChromiumCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
        => _discovery = new ChromiumUserDataDiscovery(Environment);

    public override string Id => "chromium-app-cache";

    public override string Name => "Chromium application caches";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "Each application fetches the web content it had cached and recompiles its scripts the " +
        "first time it is opened again, so it starts more slowly once. Sign-ins, saved passwords " +
        "and settings are untouched.";

    /// <summary>
    /// The applications whose folders hold at least one recognised cache, memoised for the life of
    /// a planning pass (G4). Presence and planning ask the same question of the same disk, and the
    /// walk behind it covers every directory one level under both application-data roots.
    ///
    /// Exposed so tests can assert that no user-data folder is ever a target.
    /// </summary>
    public IReadOnlyList<ChromiumUserData> Applications(CancellationToken ct = default) =>
        _applications ??= [.. _discovery.Discover(ct).Where(app => HasRecognisedCache(app, ct))];

    public override void InvalidateCaches()
    {
        _applications = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// Presence is a cache actually on disk, never a folder existing. An application that embeds
    /// Chromium but has not run yet keeps a user-data folder with no cache in it, and reporting that
    /// as a source would offer the user a row the plan then has nothing to say about.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Applications(ct).Count > 0);

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        var applications = Applications(ct);

        if (applications.Count == 0)
        {
            return EmptyPlan("No application on this machine keeps a Chromium cache in its data folder.");
        }

        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();
        var declined = new List<(string Path, string Reason)>();
        var survivors = new List<(string Path, string Reason)>();

        foreach (var application in applications)
        {
            ct.ThrowIfCancellationRequested();

            survivors.Add((
                application.Path,
                $"The '{application.Name}' data folder itself must survive — only recognised cache directories inside it are removed."));
            survivors.AddRange(ProtectedRootFiles.Select(f => (Path.Combine(application.Path, f.Name), f.Reason)));

            var spared = 0;

            foreach (var profile in application.Profiles)
            {
                survivors.Add((
                    profile,
                    "The profile directory itself must survive — only recognised cache directories inside it are removed."));
                survivors.AddRange(ProtectedProfileFiles.Select(f => (Path.Combine(profile, f.Name), f.Reason)));

                spared += CollectFrom(profile, targets, declined, survivors, notes, ct);
            }

            // One note per application rather than one per spared child. A Chromium profile holds
            // dozens of directories, so naming each of them across ten applications would produce a
            // plan nobody reads — and a note nobody reads protects nothing. Each of them is still
            // asserted individually by §5.6, and this is the sentence that says so.
            if (spared > 0)
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"In '{application.Name}', {spared} other {(spared == 1 ? "item is" : "items are")} left alone "
                    + "beside the caches. Sign-in state, saved passwords and offline data all live in that folder, "
                    + "so only the recognised cache directories are removed."));
            }
        }

        if (targets.Count == 0 && declined.Count == 0)
        {
            return EmptyPlan("No application on this machine keeps a Chromium cache in its data folder.");
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        // §5.3. The process names are not declared, because the applications are discovered rather
        // than known — so the folder's name stands in for the process's, which is right far more
        // often than not for an application that named its own data folder. It decides nothing: a
        // miss costs one absent warning, and a hit names a process the user can actually see.
        if (RunningProcessNotice.For(Inspector, [.. applications.Select(a => a.Name)]) is { } warning)
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
            // The user-data folder is its own profile in the single-profile layout, so that one
            // directory is named twice on that path. Verifying it twice would report one survivor
            // as two.
            ProtectedPaths = Protect(
                [.. survivors.Concat(declined).DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase)]),
            // A container that is a link is met twice — once as a child of the profile, once as a
            // level of its own — and says the same sentence about the same path both times.
            Notes = [.. notes.DistinctBy(n => n.Message, StringComparer.Ordinal)],
            Fallback = measured.Fallback,
        };
    }

    /// <summary>
    /// §5.2 for one profile: classify the children of each level, target the recognised ones, and
    /// protect the rest. A spared child is a sibling of a targeted one under the same parent, which
    /// is exactly when an over-broad rule takes both — so it is asserted to survive rather than
    /// merely left out of the plan.
    /// </summary>
    /// <returns>
    /// How many children were spared, which is what the per-application note reports. Counted here
    /// rather than from the length of <paramref name="survivors"/>, which also carries the folder,
    /// the profile and the named files — a total including those would tell the user that items
    /// were left alone in a folder that may not hold them.
    /// </returns>
    private static int CollectFrom(
        string profile,
        List<DeletionTarget> targets,
        List<(string Path, string Reason)> declined,
        List<(string Path, string Reason)> survivors,
        List<PlanNote> notes,
        CancellationToken ct)
    {
        var spared = 0;

        foreach (var level in Levels)
        {
            ct.ThrowIfCancellationRequested();

            var directory = level.Resolve(profile);

            if (!LongPath.DirectoryExists(directory))
            {
                continue;
            }

            // Applied at every level rather than only at the two reached by name. The profile
            // directories came from an enumeration that filtered links out, so this answers false
            // for them today — and that is the point. Phase 1's junction defect existed because the
            // safety property was riding on a filter nobody had named, and it held only for as long
            // as every target happened to arrive the same way.
            if (LongPath.IsReparsePoint(directory))
            {
                notes.Add(LinkNote(directory));
                declined.Add((directory, LinkReason));
                continue;
            }

            var scan = ChildDirectories.Under(directory);

            foreach (var link in scan.Links)
            {
                notes.Add(LinkNote(LongPath.Display(link.FullName)));
                declined.Add((LongPath.Display(link.FullName), LinkReason));
            }

            foreach (var child in scan.Directories)
            {
                var classification = level.Children.Classify(child.Name);
                var path = LongPath.Display(child.FullName);

                if (classification.Tier.IsOfferable())
                {
                    targets.Add(new DeletionTarget(path, classification.Reason));
                }
                else
                {
                    survivors.Add((path, classification.Reason));
                    spared++;
                }
            }
        }

        return spared;
    }

    private const string LinkReason =
        "A link rather than a directory, so what it points at was never classified.";

    private static PlanNote LinkNote(string path) => new(
        PlanNoteSeverity.Information,
        $"Leaving '{path}' alone: it is a link to somewhere else, and Deguffer does not delete "
        + "through a link.");

    /// <summary>
    /// Whether any of the six declared names is on disk for this application, by probing the table
    /// rather than by enumerating (G4). Six existence checks per profile, and not one of them can
    /// reach a path the table does not name.
    /// </summary>
    private static bool HasRecognisedCache(ChromiumUserData application, CancellationToken ct)
    {
        foreach (var profile in application.Profiles)
        {
            foreach (var level in Levels)
            {
                ct.ThrowIfCancellationRequested();

                var directory = level.Resolve(profile);

                if (level.Children.DisposableNames.Any(
                        name => LongPath.DirectoryExists(Path.Combine(directory, name))))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
