using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The browser builds Puppeteer downloads into the user's profile, and keeps one of for every version
/// it has installed.
///
/// <para><b>Tier 2</b>, on <see cref="PlaywrightBrowsersProvider"/>'s reasoning. Puppeteer launches
/// the build it was pinned to, and if that folder is gone the launch fails with "Could not find
/// Chrome" until somebody runs <c>npx puppeteer browsers install</c> or reinstalls the package. The
/// user is choosing a broken run followed by a deliberate re-download, so this is offered and never
/// pre-selected.</para>
///
/// <para>§5.1's commands were considered and not used. <c>@puppeteer/browsers clear</c> removes the
/// whole cache folder, the tool root §5.2 forbids, along with the metadata beside the builds, and
/// both it and <c>uninstall</c> are reached through <c>npx</c>, which fetches the package where no
/// project has it. That leaves §5.2's path-based route.</para>
///
/// <para><b>Only the <c>puppeteer</c> folder, never <c>%USERPROFILE%\.cache</c>.</b> That folder is
/// shared by unrelated tools, and on the measured machine it was mostly machine-learning models that
/// may not be published any more. This provider never lists it.</para>
///
/// <para>§5.2 reaches two levels here, as for <see cref="AzureFunctionsToolsProvider"/>. The cache
/// root recognises nothing, because each browser folder holds <c>.metadata</c> beside its builds, and
/// each browser folder recognises only the builds <see cref="PuppeteerCacheLayout"/> names for that
/// browser.</para>
/// </summary>
public sealed class PuppeteerBrowsersProvider : CleanupProviderBase
{
    /// <summary>
    /// Set by the user to relocate the cache. Puppeteer also reads <c>cacheDirectory</c> from a
    /// <c>.puppeteerrc</c> file, but that file belongs to a project and is found from the directory
    /// a script runs in, so Deguffer cannot know which one applies.
    /// </summary>
    public const string LocationVariable = "PUPPETEER_CACHE_DIR";

    /// <summary>Why a build a running program is using is listed as a survivor, in the §5.6 report.</summary>
    private const string InUseReason = "A running program is using this browser build, so it is left alone.";

    private readonly ILiveTreeInspector _liveTrees;

    public PuppeteerBrowsersProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ILiveTreeInspector? liveTrees = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
        => _liveTrees = liveTrees ?? LiveTreeInspector.Default;

    public override string Id => "puppeteer";

    public override string Name => "Puppeteer browsers";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    /// <summary>Each build is a browser at a version a project may pin.</summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "Puppeteer scripts and tests that launch a removed browser stop running until "
        + "'npx puppeteer browsers install' is run or Puppeteer is installed again, which re-downloads "
        + "a few hundred megabytes for each browser. Scripts, configuration and projects are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Puppeteer, a browser automation library for Node.js",
        Publisher = "Google",
        Purpose = "Puppeteer downloads its own builds of Chrome and Firefox rather than using the "
            + "browsers you have installed, and keeps every version it has downloaded in your profile "
            + "where every project can share them.",
        Recommendation = "Nothing here is yours: every folder is a browser build Puppeteer "
            + "downloaded and pins by version, so installing again restores exactly what was removed. "
            + "Choose a moment when you are not about to run a script or a test suite.",
    };

    /// <summary>Where Puppeteer keeps browsers when <see cref="LocationVariable"/> is unset.</summary>
    public string DefaultRoot => Path.Combine(Environment.UserProfile, ".cache", "puppeteer");

    /// <summary>
    /// The cache root, honouring <see cref="LocationVariable"/>, or null where the variable is not
    /// a full path. Puppeteer resolves a relative value against the directory the script runs in,
    /// which Deguffer is not, so there is no correct reading of it here and the default is not a
    /// silent substitute either: the user pointed Puppeteer away from it.
    /// </summary>
    public string? ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable(LocationVariable);

        return string.IsNullOrWhiteSpace(configured)
            ? DefaultRoot
            : LongPath.Configured(configured.Trim());
    }

    /// <summary>
    /// The root recognises nothing, and each browser folder recognises its own builds. The browser
    /// folders are declared whether or not they exist, because a declaration is a rule about a path,
    /// and one Puppeteer creates tomorrow is covered by it today.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
        ResolveRoot() is { } root
            ?
            [
                new ToolRoot(
                    root,
                    "This is Puppeteer's browser cache. Deguffer removes whole browser builds from the "
                    + "browser folders inside it and nothing else, because the metadata beside the "
                    + "builds is how Puppeteer finds them.",
                    static _ => false),
                .. PuppeteerCacheLayout.Browsers.Select(browser => new ToolRoot(
                    Path.Combine(root, browser),
                    $"This is where Puppeteer keeps the {browser} builds it downloaded. Deguffer removes "
                    + "whole builds from it, never the folder or the metadata beside them.",
                    name => PuppeteerCacheLayout.IsBuild(browser, name))),
            ]
            : [];

    public override void InvalidateCaches()
    {
        _liveTrees.Invalidate();
        base.InvalidateCaches();
    }

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ResolveRoot() is { } root && LongPath.DirectoryMayExist(root));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (ResolveRoot() is not { } root)
        {
            return EmptyPlan(
                $"{LocationVariable} is set to '{Environment.GetEnvironmentVariable(LocationVariable)?.Trim()}', "
                + "which is not a full path. Deguffer cannot tell which directory that means, so it is "
                + "leaving it alone.");
        }

        if (NothingToPlanFor(
                root,
                $"Puppeteer has not downloaded any browsers on this machine ({root}).") is { } nothing)
        {
            return nothing;
        }

        // The root arrives by name, and the enumeration below never classifies the directory it is
        // handed: a junctioned root would hand back the far side's ordinary directories, and every
        // survivor named for it would resolve through the same link and pass.
        if (LongPath.IsReparsePoint(root))
        {
            return UnexaminedPlan(
                $"Leaving '{root}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        var survey = PuppeteerCacheSurvey.Of(root, ct);

        var live = LiveTreeVeto.Apply(
            _liveTrees,

            // The build is its own project: a browser runs from inside its build, and a program
            // working in one browser folder is using none of the builds beside it.
            [.. survey.Builds.Select(build => new RecognisedBuildDirectory(build.Path, build.Path))],
            lockFiles: [],
            ct);

        var builds = survey.Builds.ToDictionary(build => build.Path, StringComparer.OrdinalIgnoreCase);
        var notes = new List<PlanNote>(survey.Notes);

        if (LiveTreeVeto.NoteFor(
                live.Vetoed,
                vetoed => $"the {builds[vetoed.Directory].Browser} build '{builds[vetoed.Directory].Name}'") is { } held)
        {
            notes.Add(held);
        }

        if (LiveTreeVeto.IncompleteNote(
                live.Complete,
                "Close any Puppeteer script or test run before you clean.") is { } incomplete)
        {
            notes.Add(incomplete);
        }

        var targets = live.Cleared.Select(cleared =>
        {
            var build = builds[cleared.Path];

            return new DeletionTarget(
                cleared.Path,
                $"Puppeteer {build.Browser} build '{build.Name}', downloaded again by "
                + "'npx puppeteer browsers install'.",

                // A build is written once, when it is downloaded, and launching it does not rewrite
                // its folder, so this says when it arrived.
                DirectoryAge.Of(cleared.Path, ct),

                // The browser and the build's own name are the build, wherever the variable puts the
                // cache, so a kept build stays kept when the user moves it.
                Identity: new ItemIdentity($"{build.Browser}/{build.Name}", $"{build.Browser} {build.BuildId}"),
                Facets: [new ItemFacet("Version", build.BuildId), new ItemFacet("Platform", build.Platform)],
                Group: build.Browser,
                UseCheck: cleared.StillUnused);
        }).ToList();

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = BuildProtectedPaths(root, survey, live.Vetoed),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = survey.Unreadable,
            WasNotExamined = targets.Count == 0 && (survey.Declined.Count > 0 || live.Vetoed.Count > 0),
        };
    }

    /// <summary>
    /// §5.6. The metadata is the reason this provider enumerates rather than removing a browser
    /// folder whole: it maps an alias such as <c>stable</c> to a build, and Puppeteer resolves a
    /// launch through it. Every folder declined at either level is named as well, because the spared
    /// and the removed are siblings, which is when an over-broad rule takes one with the other.
    ///
    /// <c>%USERPROFILE%\.cache</c> is asserted to survive when the cache is in its default place,
    /// since the models beside Puppeteer's folder are the costliest mistake this row could make.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(
        string root,
        PuppeteerCacheSurvey survey,
        IReadOnlyList<LiveTree> vetoed)
    {
        var survivors = new List<(string Path, string Reason)>
        {
            (root, "Puppeteer's cache folder must survive; only whole browser builds inside it are removed."),
        };

        if (root.Equals(DefaultRoot, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(root) is { } shared)
        {
            survivors.Add((shared, "The folder other tools share with Puppeteer is never touched."));
        }

        foreach (var folder in survey.BrowserFolders)
        {
            survivors.Add((folder, "A browser folder must survive; only whole builds inside it are removed."));
            survivors.Add((
                Path.Combine(folder, PuppeteerCacheLayout.MetadataName),
                "Puppeteer's record of which build each alias names and where its executable is."));
        }

        survivors.AddRange(survey.Declined);
        survivors.AddRange(vetoed.Select(v => (v.Directory, InUseReason)));

        return Protect([.. survivors]);
    }
}
