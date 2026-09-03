using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Playwright's shared browser cache (~1 GB on the audited machine).
///
/// Tier 2 rather than Tier 1, and the distinction is the point. A package cache refills itself the
/// next time the tool needs it; these binaries do not. Playwright resolves the browser it was pinned
/// to at launch, and if that directory is gone the run fails with "Executable doesn't exist" until
/// somebody runs <c>playwright install</c> by hand. The user is therefore choosing a broken test run
/// followed by a deliberate re-download, not a slower one — so this is offered, never pre-selected,
/// and §7's acknowledgement applies.
///
/// §5.1's command was considered and not used. <c>playwright uninstall</c> is a per-project binary
/// reached through <c>npx</c>, and without <c>--all</c> it evicts only the browsers belonging to the
/// installation in the current directory — which is the wrong scope for a machine-wide cleaner, and
/// unreachable from one in the common case where Playwright is a project dependency rather than a
/// global install. That leaves §5.2's path-based route.
///
/// §5.2 bites differently here from Gradle. The disposable children are versioned
/// (<c>chromium-1228</c>), so an exact-name <see cref="DisposableChildSet"/> cannot express them —
/// but the fix is a stricter test, not a looser one: a known browser name <em>and</em> a numeric
/// revision. A directory that is not both stays Tier 4.
/// </summary>
public sealed partial class PlaywrightBrowsersProvider : CleanupProviderBase
{
    /// <summary>
    /// Set by the user to relocate the browser cache. The value <c>0</c> is special: it tells
    /// Playwright to install browsers inside the project's <c>node_modules</c> instead of a shared
    /// location, so there is no machine-wide cache to offer.
    /// </summary>
    public const string LocationVariable = "PLAYWRIGHT_BROWSERS_PATH";

    /// <summary>
    /// The browser and helper artefacts Playwright downloads, each of which appears on disk as
    /// <c>{name}-{revision}</c>. This is an allow-list for the same reason
    /// <see cref="DisposableChildSet"/> is: a name this does not know is Tier 4, never a guess.
    ///
    /// <c>.links</c> is deliberately absent — see <see cref="BuildProtectedPaths"/>.
    /// </summary>
    [GeneratedRegex(
        @"\A(?:chromium|chromium_headless_shell|chromium-tip-of-tree|chromium-tip-of-tree_headless_shell|firefox|firefox-asan|firefox-beta|webkit|ffmpeg|winldd|android)-[0-9]+\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RecognisedChild();

    public PlaywrightBrowsersProvider(
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

    public override string Id => "playwright";

    public override string Name => "Playwright browsers";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "Playwright tests stop running until 'playwright install' is run again, which re-downloads " +
        "roughly a gigabyte of browser builds. Test code, configuration and reports are untouched.";

    /// <summary>
    /// Whose files these are and what they are for. See <see cref="ProviderDescription"/>.
    /// </summary>
    public override ProviderDescription Description { get; } = new()
    {
        Application = "Playwright, a browser automation and testing library",
        Publisher = "Microsoft",
        Purpose = "Playwright downloads its own builds of Chromium, Firefox and WebKit rather "
            + "than using the browsers you have installed, and keeps them in your profile where "
            + "every project can share them.",
        Recommendation = "Only when you need the space. Tests stop running until playwright "
            + "install is run again, which re-downloads roughly a gigabyte of browser builds. Test "
            + "code, configuration and reports are untouched.",
    };

    /// <summary>
    /// Deliberately narrow. Playwright drives ordinary <c>chrome.exe</c> and <c>firefox.exe</c>
    /// binaries, so warning on those would fire for the user's everyday browser on almost every
    /// scan — and a warning that is always present is one nobody reads. These two names only exist
    /// while Playwright itself is working.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["playwright", "headless_shell"];

    /// <summary>Where Playwright keeps browsers when <see cref="LocationVariable"/> is unset.</summary>
    public string DefaultRoot => Path.Combine(Environment.LocalAppData, "ms-playwright");

    /// <summary>
    /// The browser cache root, honouring <see cref="LocationVariable"/>. Null when Playwright is
    /// configured to keep browsers inside <c>node_modules</c>, where they belong to a project rather
    /// than the machine and are not this provider's business.
    /// </summary>
    public string? ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable(LocationVariable);

        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultRoot;
        }

        configured = configured.Trim();

        // "0" is Playwright's documented sentinel for per-project installs, not a path.
        if (configured == "0")
        {
            return null;
        }

        // A relative value would resolve against Deguffer's working directory rather than the one
        // the user meant, and enumerating a directory nobody pointed at is exactly the guess §5.2
        // forbids. Playwright resolves it against the test process's directory, which Deguffer is
        // not — so there is no correct interpretation available here, and offering nothing is the
        // only honest answer. LongPath.Configured also normalises what it accepts, so a trailing
        // separator or a '..' cannot reach the enumeration below.
        return LongPath.Configured(configured);
    }

    /// <summary>
    /// The browser cache root. Its children are versioned rather than named, so this carries
    /// <see cref="RecognisedChild"/> itself — which is exactly why <see cref="ToolRoot"/> holds a
    /// predicate: a declaration that could only hold names would have had to leave this provider
    /// out, and §5.2 must never fail in that direction.
    ///
    /// Empty where Playwright is configured to install into a project's <c>node_modules</c>, since
    /// there is then no machine-wide root to make a claim about.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
        ResolveRoot() is { } root
            ?
            [
                new ToolRoot(
                    root,
                    "This is Playwright's browser cache. Deguffer removes whole browser builds from "
                    + "it and nothing else, because the links and metadata beside them are what "
                    + "Playwright resolves a build through.",
                    name => RecognisedChild().IsMatch(name)),
            ]
            : [];

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ResolveRoot() is { } root && LongPath.DirectoryExists(root));

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        if (ResolveRoot() is not { } root)
        {
            var configured = Environment.GetEnvironmentVariable(LocationVariable)?.Trim();

            return EmptyPlan(configured == "0"
                ? $"{LocationVariable} is set to 0, so Playwright keeps browsers inside each project " +
                  "rather than in a shared cache."
                : $"{LocationVariable} is set to '{configured}', which is not a full path. Deguffer " +
                  "cannot tell which directory that means, so it is leaving it alone.");
        }

        if (!LongPath.DirectoryExists(root))
        {
            return EmptyPlan($"Playwright has not downloaded any browsers on this machine ({root}).");
        }

        // The root arrives by name, from an environment variable or a default, so nothing has
        // classified it. ChildDirectories.Under separates link children and never classifies the
        // directory it was handed, so a junctioned root hands back the far side's ordinary
        // directories — a recognised name among them would be targeted while every survivor named
        // for this root resolved through the same link and passed.
        if (LongPath.IsReparsePoint(root))
        {
            return EmptyPlan(
                $"Leaving '{root}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();

        var scan = ChildDirectories.Under(root);

        // The root was found on disk by name above, and a listing right is separate from a traverse
        // right — so a refusal here leaves a plan with no steps and, without this, nothing said. The
        // shell renders that as "Already clear", which is a claim about a folder nobody read.
        if (scan.Unreadable)
        {
            notes.Add(UnreadableRoot.Note(root));
        }

        // A link is a child the user can see, so it is named rather than dropped. It is never
        // followed: what it points at was never classified.
        notes.AddRange(scan.Links.Select(link => new PlanNote(
            PlanNoteSeverity.Information,
            $"Leaving '{link.Name}' alone: it is a link to somewhere else, and Deguffer does not "
            + "delete through a link.")));

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            if (!RecognisedChild().IsMatch(child.Name))
            {
                // §5.2: unrecognised means untouched, and the user is told rather than left to
                // wonder why the total is smaller than the folder.
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{child.Name}' alone: not a recognised Playwright browser download."));
                continue;
            }

            // Enumeration runs in extended form; a plan always holds display paths, and I/O
            // re-extends at the point of use.
            targets.Add(new DeletionTarget(
                LongPath.Display(child.FullName),
                $"Playwright browser build '{child.Name}', re-downloaded by 'playwright install'."));
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        if (BuildRunningProcessNote() is { } warning)
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
            ProtectedPaths = BuildProtectedPaths(root),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = scan.Unreadable,
        };
    }

    /// <summary>
    /// §5.6. <c>.links</c> is the reason this provider enumerates rather than deleting the root:
    /// it holds one marker per client installation that references these browsers, and Playwright
    /// reads it to decide when a browser version has no users left and may be removed. Deleting the
    /// browsers is a reclaim Playwright recovers from; deleting the registry that tracks them
    /// breaks its own housekeeping, and it looks exactly like a cache while doing so.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(string root) => Protect(
        (root, "The browser cache root itself must survive — only recognised browser builds are removed."),
        (Path.Combine(root, ".links"), "Playwright's record of which installations use which browsers."));
}
