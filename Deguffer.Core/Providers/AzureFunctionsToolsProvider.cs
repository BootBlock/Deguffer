using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Azure Functions Core Tools releases Visual Studio downloads for itself (599 MB on the
/// measured machine).
///
/// <para>Visual Studio and the Azure Functions extension do not use a Core Tools installation the
/// developer put on the machine. They read a feed, decide which release each Functions runtime line
/// needs, and download it into <c>%LOCALAPPDATA%\AzureFunctionsTools\Releases\&lt;version&gt;</c>.
/// Nothing ever removes one, so a machine that has followed the tooling through several updates
/// holds every release it has ever fetched, each with its own copy of the templates and the isolated
/// worker runtimes.</para>
///
/// <para><b>Tier 2</b>, on <see cref="PlaywrightBrowsersProvider"/>'s reasoning and for the same
/// cause. A package cache refills itself the next time the tool needs it; a release does not refill
/// itself so much as get fetched again, on the tooling's schedule rather than the developer's, and
/// the wait lands in the middle of opening a project. So the row is offered and never pre-selected,
/// and §7's acknowledgement applies.</para>
///
/// <para>§5.1 has no answer here rather than being skipped: neither Visual Studio nor the Core Tools
/// offers a command that evicts a downloaded release, and the tooling's documented remedy for a bad
/// download is to delete the directory. That leaves §5.2's path-based route.</para>
///
/// <para><b>Every release is offered, including the ones the tooling still points at.</b> That is
/// the judgement Playwright's browsers get, and for the same reason: Deguffer cannot know whether
/// the developer still has a Functions v2 project, and withholding 166 MB on the assumption that
/// they might is Deguffer making their decision. What it can do is say which is which, so each row
/// carries the age of the release and, where the tooling's own records reach it, whether they still
/// name it — see <see cref="AzureFunctionsToolTags"/>.</para>
///
/// <para>§5.2 bites the way it does for Playwright: the disposable children are versioned, so an
/// exact-name <see cref="DisposableChildSet"/> cannot express them, and the answer is a stricter
/// test rather than a looser one. A child of <c>Releases</c> is recognised only if its whole name is
/// a dotted numeric version. Anything else is Tier 4.</para>
/// </summary>
public sealed partial class AzureFunctionsToolsProvider : CleanupProviderBase
{
    /// <summary>The tooling's folder in the profile. Two levels are declared, so it is named once.</summary>
    public const string RootName = "AzureFunctionsTools";

    /// <summary>The folder holding one directory per downloaded release.</summary>
    public const string ReleasesName = "Releases";

    /// <summary>
    /// The feed the tooling caches beside <c>Releases</c>, one file per feed sequence. Never a
    /// target: it is how the tooling knows what it has already got.
    /// </summary>
    public const string FeedPattern = "feed-v*.json";

    /// <summary>
    /// A downloaded release directory, whose whole name is the release version — <c>4.18.1</c>,
    /// <c>2.60.0</c>, <c>4.0.5455</c>.
    ///
    /// <para><b>Exactly three parts, which is the shape the feed has always served.</b> A wider rule
    /// costs nothing to write and is the §5.2 mistake: a directory named <c>4.18</c> or <c>5</c> was
    /// made by a person, not by the tooling, and treating an unknown thing as safe is the one
    /// direction this must never fail in. If Microsoft ever renumbers, Deguffer stops offering the
    /// row and says which children it is leaving alone, which is visible and fixable — where the
    /// opposite mistake is not.</para>
    ///
    /// Anchored with <c>\A</c> and <c>\z</c> rather than <c>^</c> and <c>$</c>: <c>$</c> also matches
    /// before a trailing newline, and a check that decides whether a directory may be deleted should
    /// admit no such reading.
    /// </summary>
    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersion();

    private readonly string _root;
    private readonly string _releases;

    public AzureFunctionsToolsProvider(
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
        _root = Path.Combine(Environment.LocalAppData, RootName);
        _releases = Path.Combine(_root, ReleasesName);
    }

    public override string Id => "azure-functions-tools";

    public override string Name => "Azure Functions Core Tools releases";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next time Visual Studio opens or runs a Functions project that needs a release which "
        + "was removed, it downloads that release again before the project will start — a few "
        + "minutes and a few hundred megabytes. Your function projects and their settings are "
        + "untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Azure Functions Core Tools, as Visual Studio downloads them",
        Publisher = "Microsoft",
        Purpose = "Visual Studio and the Azure Functions extension fetch their own copy of the "
            + "Core Tools rather than using one you installed, and keep a separate copy for each "
            + "version of the Functions runtime. Nothing ever removes an old copy, so every release "
            + "the tooling has fetched is still in your profile.",
        Recommendation = "Every release here was downloaded and can be downloaded again, so what "
            + "you are choosing is a wait the next time a project needs one. Each row shows when "
            + "its release arrived, and says whether the tooling's own records still name it.",
    };

    /// <summary>
    /// §5.3. <c>func</c> is the Core Tools host, and it runs out of one of these directories while a
    /// Functions project is being debugged.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["func"];

    /// <summary>The tooling's folder. Exposed so tests can assert it is never targeted.</summary>
    public string RootPath => _root;

    /// <summary>The folder holding the releases. Exposed for the same reason.</summary>
    public string ReleasesPath => _releases;

    /// <summary>
    /// Two levels, because §5.2's declaration is an allow-list over one directory's immediate
    /// children and cannot reach deeper. The outer folder recognises <em>nothing</em>: the feed, the
    /// tag records and <c>Releases</c> itself are all things the tooling reads, and none of them is
    /// ever removed. The inner one recognises a release version and nothing else.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        new ToolRoot(
            _root,
            "This is the Azure Functions tooling's own folder. Deguffer removes whole downloaded "
            + "releases from the 'Releases' folder inside it and nothing else, because the feed and "
            + "the tag records beside them are how the tooling knows what it already has.",
            _ => false),

        new ToolRoot(
            _releases,
            "This is where the Azure Functions tooling keeps every release it has downloaded. "
            + "Deguffer removes whole releases from it, never the folder itself.",
            name => ReleaseVersion().IsMatch(name)),
    ];

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(LongPath.DirectoryExists(_root));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (!LongPath.DirectoryExists(_root))
        {
            return EmptyPlan(
                "Visual Studio has not downloaded the Azure Functions Core Tools on this machine.");
        }

        // Moving 600 MB of downloaded releases onto another drive with a junction is how a developer
        // keeps them off a small system disk, and the enumeration below never classifies the
        // directory it is handed: it would return the far side's ordinary children, target the ones
        // whose names look like versions, and pass every §5.6 assertion, because each survivor named
        // here resolves through the same link. Both levels can be moved that way, so both are asked.
        if (LongPath.IsReparsePoint(_root))
        {
            return LinkedAway(_root);
        }

        if (!LongPath.DirectoryExists(_releases))
        {
            return EmptyPlan($"The Azure Functions tooling has downloaded no releases ({_releases}).");
        }

        if (LongPath.IsReparsePoint(_releases))
        {
            return LinkedAway(_releases);
        }

        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();
        var declined = new List<(string Path, string Reason)>();

        var live = AzureFunctionsToolTags.Read(_root, ct);
        var scan = ChildDirectories.Under(_releases);

        // The folder was found on disk by name above, and a listing right is separate from a
        // traverse right — so a refusal here leaves a plan with no steps and, without this, nothing
        // said. The shell renders that as "Already clear", which is a claim about a folder nobody
        // read.
        if (scan.Unreadable)
        {
            notes.Add(UnreadableRoot.Note(_releases));
        }

        // A link is a child the user can see, so it is named rather than dropped. It is never
        // followed: what it points at was never classified.
        foreach (var link in scan.Links)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving '{link.Name}' alone: it is a link to somewhere else, and Deguffer does not "
                + "delete through a link."));

            declined.Add((
                LongPath.Display(link.FullName),
                "A link rather than a directory, so what it points at was never classified."));
        }

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            if (!ReleaseVersion().IsMatch(child.Name))
            {
                // §5.2: unrecognised means untouched, and the user is told rather than left to
                // wonder why the total is smaller than the folder.
                const string Why = "not a release the Azure Functions tooling downloaded.";

                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{child.Name}' alone: {Why}"));

                declined.Add((LongPath.Display(child.FullName), Why));
                continue;
            }

            // Enumeration runs in extended form; a plan always holds display paths, and I/O
            // re-extends at the point of use.
            targets.Add(new DeletionTarget(
                LongPath.Display(child.FullName),
                Describe(child.Name, live),

                // §7's age. A release directory is written once, when it is downloaded, and using it
                // does not rewrite it — so the answer here is when this release arrived, which is
                // what separates one the tooling fetched last week from one nobody has needed since
                // 2019.
                DirectoryAge.Of(child.FullName, ct)));
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

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
            ProtectedPaths = BuildProtectedPaths(declined),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = scan.Unreadable,
            WasNotExamined = targets.Count == 0 && declined.Count > 0,
        };
    }

    private CleanupPlan LinkedAway(string path) => UnexaminedPlan(
        $"Leaving '{path}' alone: it is a link to somewhere else, and Deguffer does not look "
        + "through a link.");

    /// <summary>
    /// What one release row says, which is the whole of what <see cref="AzureFunctionsToolTags"/>
    /// is read for.
    /// </summary>
    /// <param name="live">
    /// The tag records, or null where there were none to read. Null must not be reported as "no
    /// record names this release": that sentence is a claim about the tooling's records, and on a
    /// machine whose <c>Tags</c> folder is missing or unreadable nobody has read them.
    /// </param>
    private static string Describe(
        string version,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? live)
    {
        if (live is null)
        {
            return $"Azure Functions Core Tools {version}, downloaded by the tooling and downloaded "
                + "again if a project needs it.";
        }

        return live.TryGetValue(version, out var lines)
            ? $"Azure Functions Core Tools {version}, which the tooling's own records name as the "
                + $"release it uses for Functions {string.Join(" and ", lines)}."
            : $"Azure Functions Core Tools {version}, which the tooling's own records no longer "
                + "name. Nothing removes a superseded release on its own.";
    }

    /// <summary>
    /// §5.6. The first three are the reason this provider enumerates rather than removing a folder
    /// whole, and the feed is the subtle one — it is a cache by name and by shape, and it is what
    /// the tooling reads to work out which releases it already holds. <c>Tags</c> is the same kind of
    /// thing one folder along: it records which release each Functions runtime line uses, and this
    /// provider reads it for exactly that.
    ///
    /// Every child of <c>Releases</c> that was declined is protected by name as well, because the
    /// spared and the targeted are siblings in one folder — which is precisely when an over-broad
    /// rule takes one with the other.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(
        IReadOnlyList<(string Path, string Reason)> declined) => Protect(
    [
        (_root, "The tooling's own folder must survive — only downloaded releases inside it are removed."),
        (_releases, "The folder holding the releases must survive; only whole releases inside it go."),
        (Path.Combine(_root, AzureFunctionsToolTags.DirectoryName),
            "The tooling's record of which release each Functions runtime version uses."),
        .. FeedFiles().Select(feed =>
            (feed, "The cached feed, which is how the tooling knows what it already has.")),
        .. declined,
    ]);

    /// <summary>
    /// The cached feed files beside <c>Releases</c>, named so §5.6 can assert they survived. There
    /// is one per feed sequence the tooling has seen, so the set is found rather than known.
    /// </summary>
    private IReadOnlyList<string> FeedFiles()
    {
        try
        {
            return
            [
                .. Directory.EnumerateFiles(LongPath.Extended(_root), FeedPattern)
                    .Select(LongPath.Display),
            ];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // A folder Deguffer may not list is already reported against Releases, and the feed is
            // never a target — so failing to name it here loses a §5.6 assertion and nothing else.
            return [];
        }
    }
}
