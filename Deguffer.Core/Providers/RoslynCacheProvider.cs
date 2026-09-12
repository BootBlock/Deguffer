using System.Globalization;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The solution indexes Roslyn keeps under <c>%LOCALAPPDATA%\Microsoft\VisualStudio\Roslyn\Cache</c>
/// (538 MB on the measured machine, more than half of it in sets nothing had written to for over a year).
///
/// <para>Roslyn keeps one SQLite database per solution, grouped by the program hosting it, and the
/// program's folder is keyed by a checksum of the full path that program ran from. A program that runs from
/// a new folder therefore starts a new set, and nothing ever removes the one it left — so every update that
/// installs somewhere new strands a whole set.</para>
///
/// <para>§5.1 has nothing to prefer: Microsoft documents neither this location nor a command that evicts
/// it. So this is the path-based case, and what is recognised is taken from Roslyn's own source and from
/// what was observed on disk — see <see cref="RoslynHostDirectory"/>.</para>
///
/// <para>§5.2 bites one folder up. <c>%LOCALAPPDATA%\Microsoft\VisualStudio</c> holds each installation's
/// settings and extensions, and <c>BackupFiles</c>, which is the recovery copy of documents nobody saved.
/// It is never a target and is asserted to survive. Only a program's whole set inside <c>Cache</c> goes,
/// and only once everything in it has been recognised.</para>
///
/// <para><b>Each set is an item with its own age</b>, because the stranded sets are the ones worth removing
/// and the one in use is the one worth keeping. The user's guard on recently changed files already says
/// that without anything new here: under it, the current set's databases stay and the stranded sets go.</para>
/// </summary>
public sealed class RoslynCacheProvider : CleanupProviderBase
{
    private readonly string _visualStudio;
    private readonly string _roslyn;
    private readonly string _cache;

    public RoslynCacheProvider(
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
        _visualStudio = Path.Combine(Environment.LocalAppData, "Microsoft", "VisualStudio");
        _roslyn = Path.Combine(_visualStudio, "Roslyn");
        _cache = Path.Combine(_roslyn, "Cache");
    }

    public override string Id => "roslyn-cache";

    public override string Name => "Roslyn solution index cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    /// <summary>
    /// One program's set per item, each dated, so a set an old build left behind can be told from the one
    /// in use.
    /// </summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "Visual Studio, or the C# extension for VS Code, re-indexes each solution the next time it opens "
        + "it. Searching and navigating that code is slower until it finishes, and nothing you have "
        + "written is affected.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Roslyn, the C# and Visual Basic engine inside Visual Studio and the C# extension "
            + "for VS Code",
        Publisher = "Microsoft",
        Purpose = "Roslyn keeps an index of every solution it has opened, so that searching, navigating "
            + "and finding references work straight away when the solution is opened again. It keeps a "
            + "separate set for each program that runs it, and a program that starts running from a new "
            + "folder, as an update can, starts a new set and never removes the old one.",
        Recommendation = "Everything here is derived from source code still on your disk, and Roslyn "
            + "rebuilds what it needs the next time a solution is opened. Each row is one program's set "
            + "with the date it was last written, so a set nothing has used in a year can go while the "
            + "one in use stays.",
    };

    /// <summary>
    /// §5.3. Every program observed hosting a set here holds its databases open while a solution is loaded,
    /// so a file refused here is an ordinary outcome rather than a failure.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames =>
        ["devenv", "ServiceHub.RoslynCodeAnalysisService", "DevHub", "Microsoft.CodeAnalysis.LanguageServer"];

    /// <summary>The folder holding the sets. Exposed so tests can assert it is never targeted.</summary>
    public string CachePath => _cache;

    /// <summary>
    /// Two levels, because §5.2's declaration covers one directory's immediate children. <c>Roslyn</c>
    /// recognises nothing, so <c>Cache</c> itself is refused along with anything beside it, and <c>Cache</c>
    /// recognises a program's set only by what it holds.
    ///
    /// <para><b>That second test reads the disk</b>, where the other declarations read only a name. A name
    /// cannot vouch for anything here, and letting Explore remove on the name alone would allow what the plan
    /// calls Tier 4. It is asked only about a path inside <c>Cache</c>, and it lists a few small directories
    /// per solution.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        new ToolRoot(
            _cache,
            "This is where Roslyn keeps its solution indexes. Deguffer removes a program's whole set of "
            + "indexes from it once it recognises everything inside, and never the folder itself.",
            name => RoslynHostDirectory.Read(Path.Combine(_cache, name)) is not null),

        new ToolRoot(
            _roslyn,
            "This is Roslyn's own folder inside Visual Studio's. Deguffer removes indexes from the 'Cache' "
            + "folder inside it and nothing else.",
            _ => false),
    ];

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(LongPath.DirectoryExists(_cache));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (!LongPath.DirectoryExists(_cache))
        {
            return EmptyPlan("Roslyn has kept no solution indexes for this user.");
        }

        // Deguffer never deletes through a link, and a link at either level this provider owns would put
        // every target on the far side of one — with each §5.6 survivor resolving through the same link, so
        // proving nothing about the folder that was reasoned about.
        foreach (var level in (string[])[_roslyn, _cache])
        {
            if (LongPath.IsReparsePoint(level))
            {
                return UnexaminedPlan(
                    $"Leaving '{level}' alone: it is a link to somewhere else, and Deguffer does not look "
                    + "through a link.");
            }
        }

        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();
        var declined = new List<(string Path, string Reason)>();

        var scan = ChildDirectories.Under(_cache);

        // The folder was found on disk above, and a listing right is separate from a traverse right — so a
        // refusal here leaves a plan with no steps and, without this, nothing said. The shell renders that
        // as "Already clear", which is a claim about a folder nobody read.
        if (scan.Unreadable)
        {
            notes.Add(UnreadableRoot.Note(_cache));
        }

        // A link is a child the user can see, so it is named rather than dropped, and protected, because it
        // sits beside the sets that are removed. It is never followed: what it points at was never classified.
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

            if (RoslynHostDirectory.Read(child.FullName, ct) is not { } set)
            {
                const string Why =
                    "it does not hold only Roslyn's solution indexes, so Deguffer cannot vouch for it.";

                notes.Add(new PlanNote(PlanNoteSeverity.Information, $"Leaving '{child.Name}' alone: {Why}"));
                declined.Add((LongPath.Display(child.FullName), Why));
                continue;
            }

            targets.Add(new DeletionTarget(
                LongPath.Display(child.FullName),
                Describe(set),
                set.LastWritten,
                Facets:
                [
                    new ItemFacet("Program", set.Program),
                    new ItemFacet("Solutions", set.Solutions.ToString(CultureInfo.InvariantCulture)),
                ]));
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

    private static string Describe(RoslynHostDirectory set) =>
        $"The indexes {set.Program} kept for {set.Solutions} "
        + (set.Solutions == 1 ? "solution" : "solutions")
        + ". Roslyn rebuilds a solution's index from its source the next time the solution is opened.";

    /// <summary>
    /// §5.6. <c>BackupFiles</c> is named on its own because it is the trap one folder up: recovery copies of
    /// unsaved documents, whose loss is permanent, beside a cache that is harmless to lose. Every child of
    /// <c>Cache</c> that was declined is protected by name as well, because the spared and the removed are
    /// siblings of one shape — precisely when an over-broad rule takes one with the other.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(
        IReadOnlyList<(string Path, string Reason)> declined) => Protect(
    [
        (_visualStudio, "Visual Studio's own folder must survive: it holds each installation's settings and "
            + "extensions, and the recovery copies of documents that were never saved."),
        (Path.Combine(_visualStudio, "BackupFiles"),
            "Visual Studio's recovery copies of documents that were never saved."),
        (_roslyn, "Roslyn's folder must survive — only indexes inside its Cache folder are removed."),
        (_cache, "The folder holding the indexes must survive; only whole sets inside it go."),
        .. declined,
    ]);
}
