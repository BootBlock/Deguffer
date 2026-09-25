using System.Text.RegularExpressions;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The browser profiles Playwright and Puppeteer make in the temporary folder for each browser they
/// launch, and leave there when a test run is stopped hard (1,862 of them, 6.7 GB, on the surveyed
/// machine, the oldest 70 days old).
///
/// <para><b>Recognised by name, one child of a temporary folder at a time, and nothing else there
/// is touched.</b> Each tool builds the name itself and hands it to Node's <c>mkdtemp</c>, which
/// adds six random letters and digits: <c>playwright_chromiumdev_profile-</c>,
/// <c>playwright_firefoxdev_profile-</c> and <c>playwright_webkitdev_profile-</c> from Playwright's
/// <c>browserType.ts</c>, and <c>puppeteer_dev_chrome_profile-</c> and
/// <c>puppeteer_dev_firefox_profile-</c> from Puppeteer's launcher. A temporary folder belongs to no
/// tool, so this is §5.2 applied to a folder with no root to protect: only a child a rule can
/// attribute to a tool is a target, and every sibling is somebody else's.</para>
///
/// <para><b>Tier 1.</b> A profile made for one launch holds nothing any later launch reads: the tool
/// makes a new one each time and deletes it when the browser closes, so what is left is a profile
/// whose deletion never ran. §5.1 has no command to prefer, because that deletion is an exit hook
/// inside the test runner rather than something a cleaner can call — which is exactly why these
/// accumulate.</para>
///
/// <para><b>Its own row rather than part of <see cref="PlaywrightBrowsersProvider"/>'s</b>, which
/// is Tier 2: a browser build costs a deliberate re-download, and a leaked profile costs nothing. It
/// is also not only Playwright's.</para>
///
/// <para><b>§5.3 applies in full, because these are in a temporary folder.</b> A test that is
/// running has one of these open, so both of its requirements are met here:</para>
/// <list type="bullet">
/// <item>The temporary-files age limit, which the plan carries as its <see cref="CleanupPlan.Keep"/>
/// exactly as the temporary files row does. See <see cref="TemporaryAgeLimit"/>.</item>
/// <item>A profile a running browser was started with is never a target.
/// <see cref="ILiveTreeInspector.FindLaunchedWith"/> is what sees that: the browser runs from its
/// own install and works wherever the test runner does, and names the profile only on its command
/// line. Anything running from or working in a profile is excluded as well.</item>
/// </list>
///
/// <para><b>Presence needs no toolchain</b>, and deliberately. The name identifies its tool, and
/// the machine with abandoned profiles is often one where the tool has since been removed.</para>
///
/// <para>Playwright's WebKit never receives its profile unless the context is persistent, so a
/// WebKit profile is normally empty. It is still a leftover, and is offered as one.</para>
///
/// <para>This row and the temporary files row both reach these profiles, as NuGet's scratch folder
/// is reached by two rows. See <see cref="RunChanges"/>.</para>
/// </summary>
public sealed partial class TestBrowserProfileProvider : CleanupProviderBase
{
    /// <summary>
    /// A profile's name: its tool's prefix, then exactly the six characters <c>mkdtemp</c> adds.
    /// Case-sensitive, because the tools write the prefix in one case and nothing else writes it.
    /// </summary>
    [GeneratedRegex(
        @"\A(?:(?<tool>playwright)_(?<browser>chromium|firefox|webkit)dev_profile|(?<tool>puppeteer)_dev_(?<browser>chrome|firefox)_profile)-[A-Za-z0-9]{6}\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex RecognisedProfile();

    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.Ordinal)
    {
        ["playwright"] = "Playwright",
        ["puppeteer"] = "Puppeteer",
        ["chromium"] = "Chromium",
        ["chrome"] = "Chrome",
        ["firefox"] = "Firefox",
        ["webkit"] = "WebKit",
    };

    private readonly ILiveTreeInspector _liveTrees;
    private readonly ISystemDirectories _system;
    private readonly ICurrentPreferences _preferences;
    private IReadOnlyList<FolderScan>? _scans;

    public TestBrowserProfileProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        ILiveTreeInspector? liveTrees = null,
        ICurrentPreferences? preferences = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _system = system ?? SystemDirectories.Current;
        _liveTrees = liveTrees ?? LiveTreeInspector.Default;
        _preferences = preferences ?? DefaultPreferences.Instance;
    }

    public override string Id => "test-browser-profiles";

    public override string Name => "Test browser profiles";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Nothing changes for your tests: each run makes a new profile for every browser it starts, and "
        + "nothing reads an old one again. "
        + (TemporaryAgeLimit.Days(_preferences) is var days and > 0
            ? $"Only profiles nothing has touched for {TemporaryAgeLimit.Describe(days)} are offered, "
            : "")
        + "and a profile a running browser was started with is left where it is.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Playwright and Puppeteer, browser automation and testing libraries",
        Publisher = "Microsoft (Playwright) and Google (Puppeteer)",
        Purpose = "Each time a test starts a browser, the library makes a fresh profile for it in "
            + "your temporary folder and deletes it when the browser closes. A test run that is "
            + "stopped part way — a cancelled build, a debugger stopped mid-suite, a crash — never "
            + "gets to delete it, and nothing else ever does.",
        Recommendation = "Nothing here is yours. Each profile was made for one browser launch, and "
            + "no later run can use it.",
    };

    /// <summary>
    /// Only the headless shell, whose name exists only while a test runs it. A test drives ordinary
    /// <c>chrome.exe</c> and <c>firefox.exe</c> too, and a warning on the user's everyday browser
    /// would be on every scan. <c>headless_shell</c> is what older Playwright releases shipped.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["chrome-headless-shell", "headless_shell"];

    public override void InvalidateCaches()
    {
        _liveTrees.Invalidate();
        _scans = null;
        base.InvalidateCaches();
    }

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Scans.Any(s => s.Children.Directories.Count > 0 || s.Children.Links.Count > 0));

    /// <summary>
    /// Every profile a running program is using, as a root recognising no child, so Explore refuses
    /// it as the plan does (§7.1).
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var live = FindLive(Profiles(), ct);

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. live.Live.Select(held => new ToolRoot(
                held.Directory,
                $"A running browser is using this test profile ({string.Join("; ", held.Holders)}). "
                + "Deguffer leaves it alone until the browser has closed.",
                static _ => false)),
        ]);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var days = TemporaryAgeLimit.Days(_preferences);

        // Fixed once, so the preview and the clean agree about which files are old enough however
        // long the preview sits on screen. A limit of zero is MinimumAge.Off.
        var floor = MinimumAge.Within(TimeSpan.FromDays(days), DateTime.UtcNow);
        var effective = MinimumAge.Stricter(keep, floor);

        var notes = new List<PlanNote>();
        var scans = Scans;

        foreach (var scan in scans)
        {
            if (scan.Children.Unreadable)
            {
                notes.Add(UnreadableRoot.Note(scan.Folder));
            }

            // A link is named rather than dropped, and never followed: what it points at was never
            // classified.
            notes.AddRange(scan.Children.Links.Select(link => new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving '{link.Name}' alone: it is a link to somewhere else, and Deguffer does not "
                + "delete through a link.")));
        }

        var profiles = Profiles();
        var unreadable = scans.Any(s => s.Children.Unreadable);

        if (profiles.Count == 0)
        {
            var empty = EmptyPlan("No test run has left a browser profile in a temporary folder.");

            return empty with
            {
                Notes = [.. empty.Notes, .. notes],
                HasUnreadableRoot = unreadable,
                WasNotExamined = scans.Any(s => s.Children.Links.Count > 0),
            };
        }

        var live = FindLive(profiles, ct);
        var targets = new List<DeletionTarget>();

        foreach (var profile in profiles)
        {
            ct.ThrowIfCancellationRequested();

            if (live.IsLive(profile.Path))
            {
                continue;
            }

            targets.Add(new DeletionTarget(
                profile.Path,
                $"A {profile.Group} profile a test run left behind. Nothing reads it again.",
                LastWritten: profile.LastWritten,
                RequiresElevation: profile.RequiresElevation,

                // The path is the leftover. An empty WebKit profile frees no bytes, and removing it
                // is still the whole of what this row is for.
                IsLeftover: true,
                Group: profile.Group));
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, effective, ct).ConfigureAwait(false);

        if (floor.IsOn)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Only profiles nothing has touched for {floor.Describe()} are offered, which is your "
                + "limit for temporary files. A test that is running now has a profile open."));
        }

        if (LiveTreeVeto.NoteFor(live.Live, l => Path.GetFileName(Path.TrimEndingDirectorySeparator(l.Directory)))
            is { } busy)
        {
            notes.Add(busy);
        }

        if (LiveTreeVeto.IncompleteNote(
            live.Complete, "Close any browser a test started before cleaning.") is { } incomplete)
        {
            notes.Add(incomplete);
        }

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        if (BuildRunningProcessNote() is { } running)
        {
            notes.Add(running);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = Protect(
            [
                .. scans.Select(s => (s.Folder, "The temporary folder itself must survive — only the "
                    + "test browser profiles inside it are removed.")),
                .. live.Live.Select(l => (l.Directory,
                    $"Left alone because {string.Join("; ", l.Holders)}.")),
            ]),
            Notes = notes,

            // §5.3's floor travels on the plan, so the removal applies exactly the cut-off the
            // preview measured against.
            Keep = effective,
            Fallback = measured.Fallback,
            HasUnreadableRoot = unreadable,
        };
    }

    /// <summary>
    /// The temporary folders and what each holds that looks like a profile, listed once per planning
    /// pass because the presence probe, Explore and the plan all ask.
    ///
    /// <para>The folders are the temporary files row's own, so a <c>%TEMP%</c> pointed somewhere
    /// Deguffer declines to treat as a temporary folder is declined here as well, and that row says
    /// why.</para>
    /// </summary>
    private IReadOnlyList<FolderScan> Scans => _scans ??=
    [
        .. from root in TempRoots.Resolve(Environment, _system).Roots
           from location in root.Locations
           let folder = Path.Combine(root.Path, location.RelativePath)
           select new FolderScan(
               folder,
               root.RequiresElevation,
               ChildDirectories.Under(folder, static name => RecognisedProfile().IsMatch(name))),
    ];

    private IReadOnlyList<Profile> Profiles() =>
    [
        .. from scan in Scans
           from directory in scan.Children.Directories
           let match = RecognisedProfile().Match(directory.Name)
           select new Profile(
               LongPath.Display(directory.FullName),
               $"{DisplayNames[match.Groups["tool"].Value]} {DisplayNames[match.Groups["browser"].Value]}",
               directory.LastWriteTimeUtc,
               scan.RequiresElevation),
    ];

    /// <summary>
    /// What is using any of <paramref name="profiles"/>: a browser started with one, or a program
    /// running from or working in one.
    /// </summary>
    private LiveTreeFindings FindLive(IReadOnlyList<Profile> profiles, CancellationToken ct)
    {
        if (profiles.Count == 0)
        {
            return LiveTreeFindings.Nothing;
        }

        var launched = _liveTrees.FindLaunchedWith([.. profiles.Select(p => p.Path)], ct);
        var occupied = _liveTrees.FindLiveChildren([.. Scans.Select(s => s.Folder)], ct);
        var named = profiles.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new LiveTreeFindings(
            [
                .. launched.Live,
                .. occupied.Live.Where(l => named.Contains(l.Directory) && !launched.IsLive(l.Directory)),
            ],
            launched.Complete && occupied.Complete);
    }

    private sealed record FolderScan(string Folder, bool RequiresElevation, ChildDirectoryScan Children);

    private sealed record Profile(string Path, string Group, DateTime LastWritten, bool RequiresElevation);
}
