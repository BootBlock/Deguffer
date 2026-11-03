using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The browser builds VS Code extensions download for themselves and keep in their own storage (747 MB
/// for one Chromium revision on the measured machine, two thirds of Cline's whole storage folder).
///
/// <para><b>Tier 1, where <see cref="PuppeteerBrowsersProvider"/> is Tier 2.</b> The builds are the
/// same kind of download from the same source, but the extension resolves its browser through
/// <c>puppeteer-chromium-resolver</c>, which downloads the build again by itself whenever it is
/// missing. The next use is a wait, never a failure that needs a command from the user, and that is
/// what Tier 1 promises.</para>
///
/// <para><b>§5.2 reaches deep into the editor's <c>User</c> folder, which is otherwise Tier 4
/// whole.</b> Beside the cache sit the extension's conversation history and its shadow-git
/// checkpoints, so this never targets the extension's folder or <c>puppeteer</c>.
/// <see cref="VsCodeExtensionBrowserDiscovery"/> finds the cache by its marker,
/// <see cref="PuppeteerCacheSurvey"/> recognises the builds inside it by Puppeteer's own rules, and
/// everything else at every level is named as a survivor.</para>
///
/// <para>§5.1 does not apply: neither the extension nor the resolver has an eviction command.</para>
/// </summary>
public sealed class VsCodeExtensionBrowsersProvider : CleanupProviderBase
{
    /// <summary>Why a build a running program is using is listed as a survivor, in the §5.6 report.</summary>
    private const string InUseReason = "A running program is using this browser build, so it is left alone.";

    private readonly VsCodeExtensionBrowserDiscovery _discovery;
    private readonly ILiveTreeInspector _liveTrees;
    private readonly PassMemo<ExtensionBrowserSearch> _search = new();

    public VsCodeExtensionBrowsersProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ILiveTreeInspector? liveTrees = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner)
    {
        _discovery = new VsCodeExtensionBrowserDiscovery(new VsCodeUserDataDiscovery(Environment));
        _liveTrees = liveTrees ?? LiveTreeInspector.Default;
    }

    public override string Id => "vscode-extension-browsers";

    public override string Name => "VS Code extension browsers";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    /// <summary>Each build is one browser one extension downloaded.</summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "The extension downloads its browser again the next time it opens one, which is several hundred "
        + "megabytes. Its conversations, checkpoints and settings are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "VS Code extensions that drive a browser of their own, such as Cline, in VS Code "
            + "and the editors built on it",
        Publisher = "Each extension's own publisher",
        Purpose = "An extension that lets an assistant use a web browser downloads its own build of "
            + "Chromium the first time it needs one, and keeps it in the extension's storage inside "
            + "the editor's folder.",
        Recommendation = "The extension downloads the browser again by itself the next time it needs "
            + "one, so the cost is a wait then. Deguffer removes only the browser builds: the "
            + "extension's conversation history, checkpoints and settings sit beside them and are left "
            + "alone.",
    };

    /// <summary>
    /// §5.2 as §7.1 reads it, for every cache found: the cache root recognises nothing, and each
    /// browser folder recognises only its own builds. Nothing above the cache root is declared,
    /// because nothing there is ever offered, and <see cref="VsCodeCacheProvider"/> already refuses the
    /// editor's <c>User</c> folder that holds it all.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        .. Search().Caches.SelectMany(cache => PuppeteerCacheLayout.Browsers
            .Select(browser => ToolRoot.Folders(
                Path.Combine(cache.Root, browser),
                $"This is where the {cache.Extension} extension keeps the {browser} builds it "
                + "downloaded. Deguffer removes whole builds from it, never the folder or what is "
                + "beside them.",
                name => PuppeteerCacheLayout.IsBuild(browser, name)))
            .Prepend(new ToolRoot(
                cache.Root,
                $"This is the {cache.Extension} extension's browser cache. Deguffer removes whole "
                + "browser builds from the browser folders inside it and nothing else.",
                static _ => false))),
    ];

    /// <summary>Each browser folder of every cache found, where the builds are, and nothing above it.</summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            .. Search(ct).Caches.SelectMany(cache => PuppeteerCacheLayout.Browsers
                .Select(browser => CleanedPlace.Whole(Path.Combine(cache.Root, browser)))),
        ]);

    public override void InvalidateCaches()
    {
        _search.Forget();
        _liveTrees.Invalidate();
        base.InvalidateCaches();
    }

    /// <summary>
    /// A cache with something in it, never one merely existing: the resolver creates the folder before
    /// its first download. A refusal or a link on the way counts as present, because the plan is what
    /// says so, and a row that never appears is the one state nothing downstream can correct.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(HasAnythingToSay(Search(ct)));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var search = Search(ct);

        if (!HasAnythingToSay(search))
        {
            return EmptyPlan("No editor extension on this machine has downloaded a browser of its own.");
        }

        var builds = search.Caches
            .SelectMany(cache => cache.Survey.Builds.Select(build => (Cache: cache, Build: build)))
            .ToDictionary(found => found.Build.Path, StringComparer.OrdinalIgnoreCase);

        // A browser the extension launched runs from inside its build, and removing the build under
        // it fails part of the way through on the files it holds open. Each build is its own project,
        // because a browser running from one is using none of the builds beside it.
        var live = LiveTreeVeto.Apply(
            _liveTrees,
            [.. builds.Keys.Select(build => new RecognisedBuildDirectory(build, build))],
            lockFiles: [],
            ct);

        var notes = new List<PlanNote>(search.Notes.Concat(search.Caches.SelectMany(cache => cache.Survey.Notes)));

        if (LiveTreeVeto.NoteFor(
                live.Vetoed,
                vetoed => $"the {builds[vetoed.Directory].Build.Browser} build '{builds[vetoed.Directory].Build.Name}'") is { } held)
        {
            notes.Add(held);
        }

        if (LiveTreeVeto.UncheckedNote(live.Unchecked.Count) is { } incomplete)
        {
            notes.Add(incomplete);
        }

        var targets = live.Cleared.Select(cleared =>
        {
            var (cache, build) = builds[cleared.Path];

            return new DeletionTarget(
                cleared.Path,
                $"The {build.Browser} build '{build.Name}' the {cache.Extension} extension downloaded. "
                + "It downloads the build again the next time it opens a browser.",

                // A build is written once, when it is downloaded, so this says when it arrived.
                DirectoryAge.Of(cleared.Path, ct),

                // The editor, the extension and the build are the build, so a kept one stays kept
                // however many editors hold the same extension.
                Identity: new ItemIdentity(
                    $"{cache.Editor.Name}/{cache.Extension}/{build.Browser}/{build.Name}",
                    $"{cache.Extension} {build.Browser} {build.BuildId}"),
                Facets: [new ItemFacet("Version", build.BuildId), new ItemFacet("Platform", build.Platform)],
                Group: $"{cache.Extension} in {cache.Editor.Name}",
                UseCheck: cleared.StillUnused);
        }).ToList();

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        var declined = search.Caches.Sum(cache => cache.Survey.Declined.Count) + search.Declined.Count;

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = BuildProtectedPaths(search, live),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = search.Unreadable || search.Caches.Any(cache => cache.Survey.Unreadable),
            WasNotExamined = targets.Count == 0 && (declined > 0 || live.Held.Count > 0),
        };
    }

    /// <summary>The search, memoised for the life of a planning pass (G4): presence, planning and the declarations all ask it.</summary>
    private ExtensionBrowserSearch Search(CancellationToken ct = default) => _search.Get(() => _discovery.Search(ct));

    private static bool HasAnythingToSay(ExtensionBrowserSearch search) =>
        search.Unreadable
        || search.Declined.Count > 0
        || search.Caches.Any(cache =>
            cache.Survey.Unreadable || cache.Survey.BrowserFolders.Count > 0 || cache.Survey.Declined.Count > 0);

    /// <summary>
    /// §5.6 at every level between the extension's folder and the builds. The extension's folder and
    /// <c>puppeteer</c> are each asserted with every other folder in them, because what sits beside
    /// the cache is the extension's conversation history and its checkpoints, and a rule one level
    /// too wide would take them with it. Inside the cache, the root and what
    /// <see cref="PuppeteerCacheSurvey.Survivors"/> names.
    /// </summary>
    private static IReadOnlyList<ProtectedPath> BuildProtectedPaths(ExtensionBrowserSearch search, LiveTreeVetoResult live)
    {
        var survivors = new List<(string Path, string Reason)>(search.Declined);

        foreach (var cache in search.Caches)
        {
            var puppeteer = Path.GetDirectoryName(cache.Root)!;

            survivors.Add((
                cache.ExtensionFolder,
                $"The {cache.Extension} extension's own storage. What it saved is in here, and only "
                + "the browser builds it downloaded are removed."));
            survivors.AddRange(Beside(
                cache.ExtensionFolder,
                puppeteer,
                $"Something the {cache.Extension} extension saved, such as its conversation history "
                + "or its checkpoints."));
            survivors.Add((
                puppeteer,
                "The folder the extension downloads its browser into must survive; only whole builds "
                + "inside it are removed."));
            survivors.AddRange(Beside(
                puppeteer,
                cache.Root,
                "Not part of the browser cache, so it is left alone."));
            survivors.Add((
                cache.Root,
                "The extension's browser cache must survive; only whole builds inside it are removed."));
            survivors.AddRange(cache.Survey.Survivors);
        }

        survivors.AddRange(live.Vetoed.Select(v => (v.Directory, InUseReason)));
        survivors.AddRange(live.Unchecked.Select(path => (path, LiveTreeVeto.UncheckedReason)));

        return Protect([.. survivors]);
    }

    /// <summary>
    /// Every directory and link in <paramref name="folder"/> other than <paramref name="kept"/>, each
    /// with <paramref name="reason"/>. A folder that will not be listed names nothing here, and the
    /// folder itself is still asserted by the caller.
    /// </summary>
    private static IEnumerable<(string Path, string Reason)> Beside(string folder, string kept, string reason)
    {
        var scan = ChildDirectories.Under(folder);

        return scan.Directories
            .Concat(scan.Links)
            .Select(entry => LongPath.Display(entry.FullName))
            .Where(path => !path.Equals(kept, StringComparison.OrdinalIgnoreCase))
            .Select(path => (path, reason));
    }
}
