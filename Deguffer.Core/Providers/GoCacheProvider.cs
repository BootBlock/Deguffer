using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Go's build cache and module cache. Researched rather than measured: no Go toolchain was
/// installed on the machine this was written against.
///
/// <para><b>§5.1 in its cleanest form, and here it also closes a trap.</b> Go ships a command for
/// each location — <c>go clean -cache</c> and <c>go clean -modcache</c> — so neither is deleted by
/// path. That matters more than usual for the module cache, which Go deliberately makes read-only:
/// every extracted module file is marked so a build cannot mutate a dependency in place, and a
/// path-based remover meets an access-denied refusal per entry, which §5.3 says to treat as normal
/// and skip. A provider written that way would reclaim nothing while reporting success. Go's own
/// command is what knows how to take the cache apart, and it stays the route whatever Deguffer's
/// remover can cope with.</para>
///
/// <para><b>Neither location may be assumed, and the two answers are independent.</b>
/// <c>GOCACHE</c>, <c>GOMODCACHE</c> and <c>GOPATH</c> all move separately, through the environment
/// and through <c>go env -w</c>, so <c>go env</c> is the only authority on where they are. One
/// invocation asks for all three, which is also what tells this provider which neighbours to assert
/// survived.</para>
///
/// <para><b>Tier 1, on the same reading npm, NuGet and pip already ship under.</b> The next build
/// refills both locations by itself, with no command the user has to run and nothing to
/// re-configure. The residual is real and is stated rather than tiered around: a module from a
/// private or unreachable host — anything matching <c>GOPRIVATE</c>, or behind a proxy that is
/// down — comes back only while that host is available, and no provider can tell those entries
/// apart from public ones by looking at the filesystem.</para>
/// </summary>
public sealed class GoCacheProvider : CleanupProviderBase
{
    /// <summary>
    /// The three locations asked for in one <c>go env</c> invocation, in the order the answers come
    /// back. Kept as one array because the parsing depends on that order.
    /// </summary>
    private static readonly string[] QueriedVariables = ["GOCACHE", "GOMODCACHE", "GOPATH"];

    private GoLocations? _locations;

    public GoCacheProvider(
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

    public override string Id => "go";

    public override string Name => "Go build and module caches";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next go build downloads the modules it needs again and recompiles every package from "
        + "source, so it takes noticeably longer once and then behaves as before. Your own code, the "
        + "Go toolchain and anything installed with 'go install' are untouched. A module from a "
        + "private or unreachable host can only come back while that host is available.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Go toolchain",
        Publisher = "Google, and the Go project",
        Purpose = "Go keeps two caches shared by every project on the machine: the module cache, "
            + "holding the source of each dependency version it downloaded, and the build cache, "
            + "holding the compiled result of every package it has built.",
        Recommendation = "Deguffer runs go clean rather than deleting paths, and the toolchain "
            + "re-downloads modules and recompiles packages as the next build needs them. Your own "
            + "code and your go.mod files are untouched.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["go", "gopls", "dlv"];

    /// <summary>Where Go keeps its build cache when it has not been asked.</summary>
    public string DefaultBuildCache => Path.Combine(Environment.LocalAppData, "go-build");

    /// <summary>Where Go keeps its workspace when it has not been asked.</summary>
    public string DefaultGoPath => Path.Combine(Environment.UserProfile, "go");

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside, one root per level on Cargo's reasoning: the module
    /// cache is <c>pkg\mod</c> inside the workspace, and a declaration is an allow-list over one
    /// directory's immediate children. Declaring only the workspace would refuse <c>pkg</c>, and
    /// <c>pkg\mod</c> with it, which is the one directory <c>go clean -modcache</c> empties.
    ///
    /// <para>These are the documented defaults, which is all this property may cost. The workspace
    /// <c>go env</c> reports is declared in <see cref="DiscoverToolRootsAsync"/> instead. The build
    /// cache is not declared at all, because it is the cache itself rather than a folder with
    /// configuration beside it.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots => Declare(DefaultGoPath);

    /// <summary>
    /// The workspaces this machine actually has, which <c>GOPATH</c> lists and which move through the
    /// environment and through <c>go env -w</c>. For each, the three paths the plan asserts must
    /// survive: the workspace, the programs <c>go install</c> put in its <c>bin</c>, and the user's
    /// own source in its <c>src</c>.
    ///
    /// <para><b>Not <see cref="Declare"/>, which refuses everything in the workspace.</b> That shape
    /// suits the default, a folder named for Go. A reported workspace can be any folder, the profile
    /// itself included, and a root refusing its unrecognised children would then refuse the whole
    /// profile. So a reported workspace recognises every child and refuses only itself,
    /// <c>bin</c> and <c>src</c> refuse everything in them, and <c>pkg</c> keeps the default's rule.
    /// Where the workspace is the default, <see cref="ToolRoots"/> still refuses the rest, because a
    /// probed declaration only ever narrows what Explore allows.</para>
    /// </summary>
    public override async Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(
        CancellationToken ct = default)
    {
        if (Environment.FindExecutable("go") is not { } go)
        {
            return [];
        }

        var located = await ResolveLocationsAsync(go, ct).ConfigureAwait(false);

        return [.. located.GoPaths.SelectMany(DeclareReported)];
    }

    /// <summary>§5.2 over the default Go workspace, one root per level on Cargo's reasoning.</summary>
    private static IReadOnlyList<ToolRoot> Declare(string goPath) =>
    [
        new ToolRoot(
            goPath,
            "This is your Go workspace. Deguffer clears the module cache inside it and nothing "
            + "else, because the binaries you installed with 'go install' and your own source sit "
            + "beside it.",
            static _ => false),

        PackageRoot(goPath),
    ];

    /// <summary>The paths a reported workspace's plan protects. See <see cref="DiscoverToolRootsAsync"/>.</summary>
    private static IEnumerable<ToolRoot> DeclareReported(string workspace) =>
    [
        new ToolRoot(
            workspace,
            "This is a Go workspace, holding the programs you installed with 'go install' and your own "
            + "source. Explore removes things from inside it, never the workspace itself.",
            static _ => true),

        new ToolRoot(
            Path.Combine(workspace, "bin"),
            "These are the programs you installed with 'go install', which are usually on your PATH. "
            + "Deguffer never removes them.",
            static _ => false),

        new ToolRoot(
            Path.Combine(workspace, "src"),
            "This is source kept in your Go workspace, which is your own code. Deguffer never removes it.",
            static _ => false),

        PackageRoot(workspace),
    ];

    private static ToolRoot PackageRoot(string workspace) => new(
        Path.Combine(workspace, "pkg"),
        "This is inside your Go workspace. Deguffer clears the module cache in there and "
        + "nothing else, and leaves whatever Go keeps beside it alone.",
        static name => name.Equals("mod", StringComparison.OrdinalIgnoreCase));

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("go") is not null);

    /// <summary>
    /// Every answer here is configuration — <c>go env -w</c> writes them to a file Go reads on every
    /// run — so a remembered location would measure a directory Go has stopped using and would hand
    /// the command a stale neighbour to protect.
    /// </summary>
    public override void InvalidateCaches()
    {
        _locations = null;
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (Environment.FindExecutable("go") is not { } go)
        {
            return EmptyPlan("Go is not installed on this machine.");
        }

        var located = await ResolveLocationsAsync(go, ct).ConfigureAwait(false);
        var (buildCache, moduleCache) = (located.BuildCache, located.ModuleCache);

        var found = ReachedDirectories.Of([buildCache, moduleCache]);

        if (NothingToPlanFor(
                found,
                $"Go is installed but has cached nothing yet ({buildCache} and {moduleCache} are both absent).")
            is { } nothing)
        {
            return nothing;
        }

        // One entry per location Go actually has on disk, so a machine that has built but never
        // downloaded a module gets one step rather than a second that would reclaim nothing. A cache
        // Windows would not describe gets no step either: nothing could measure what it would free.
        var locations = new List<(string Path, string Arguments, string What)>();

        if (found.Present.Contains(buildCache))
        {
            locations.Add((
                buildCache,
                "clean -cache",
                "Clear the Go build cache using Go's own command"));
        }

        if (found.Present.Contains(moduleCache))
        {
            locations.Add((
                moduleCache,
                "clean -modcache",
                "Clear the Go module cache using Go's own command"));
        }

        var measured = await MeasureAllAsync([.. locations.Select(l => l.Path)], ct).ConfigureAwait(false);

        // Zipped rather than indexed: pairing a location with the wrong size would attribute one
        // cache's bytes to the other command, and nothing downstream could tell.
        var steps = locations
            .Zip(measured.Sizes, (location, size) => (CleanupStep)new RunCommandStep(go, location.Arguments, location.What)
            {
                Estimated = size,
                MeasuredPaths = [location.Path],
            })
            .ToList();

        var notes = new List<PlanNote>
        {
            new(PlanNoteSeverity.Information, located.Answered
                // Said two ways, because they are two different claims. The first reports this
                // machine's configuration; the second admits to a guess, which matters because a
                // machine whose caches have been moved will not match it and the user is the only
                // one who can tell.
                ? $"Go reports its build cache as {buildCache} and its module cache as {moduleCache}."
                : "Go did not say where its caches are, so these are the documented defaults rather "
                  + $"than this machine's settings: {buildCache} and {moduleCache}."),
        };

        notes.AddRange(found.UnreachedNotes);

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
            ProtectedPaths = BuildProtectedPaths(located.GoPaths),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = found.CouldNotBeReached,
        };
    }

    /// <summary>
    /// §5.6. The module cache is <c>pkg\mod</c> inside the Go workspace by default, so what
    /// <c>go clean -modcache</c> empties has the user's installed binaries and their own source
    /// tree as siblings. Those are the paths a command reaching one directory too far would take,
    /// and they are what the run has to prove it left standing, in every workspace <c>GOPATH</c>
    /// lists.
    /// </summary>
    private static IReadOnlyList<ProtectedPath> BuildProtectedPaths(IReadOnlyList<string> workspaces) => Protect(
    [
        .. workspaces.SelectMany(workspace => new[]
        {
            (workspace, "The Go workspace itself must survive — only the caches inside it are cleared."),
            (Path.Combine(workspace, "bin"), "Binaries installed with 'go install', which are normally on PATH."),
            (Path.Combine(workspace, "src"), "Source Go keeps in the workspace, which is the user's own code."),
        }),
    ]);

    /// <summary>
    /// Ask Go where it keeps things. <c>go env</c> given several names prints one value per line in
    /// the order it was asked, so the answers are read positionally — and a line that is not a
    /// rooted path is treated as no answer at all, which is what an unset or disabled value looks
    /// like.
    /// </summary>
    private async Task<GoLocations> ResolveLocationsAsync(string go, CancellationToken ct)
    {
        if (_locations is not null)
        {
            return _locations;
        }

        var outcome = await Runner
            .RunAsync(go, "env " + string.Join(' ', QueriedVariables), ct)
            .ConfigureAwait(false);

        var reported = outcome.Succeeded
            ? outcome.StandardOutput.Split('\n', StringSplitOptions.TrimEntries)
            : [];

        var buildCache = Reported(reported, 0);
        var moduleCache = Reported(reported, 1);
        var workspaces = Workspaces(reported);

        return _locations = new GoLocations(
            buildCache ?? DefaultBuildCache,

            // Go's own default for a list: the module cache is pkg\mod in the first workspace.
            moduleCache ?? Path.Combine(workspaces[0], "pkg", "mod"),
            workspaces,
            Answered: buildCache is not null && moduleCache is not null);
    }

    /// <summary>
    /// The workspaces <c>GOPATH</c> names, in Go's order.
    ///
    /// <para><b>A list, not a path.</b> On Windows <c>GOPATH</c> is separated by semicolons, and
    /// <c>go env</c> prints it as it was set, so a machine with two workspaces answers with one line
    /// that names no directory. Read whole, that line gave the plan a <c>bin</c> and a <c>src</c>
    /// that do not exist to assert, and gave Explore a workspace to refuse that matches nothing, so
    /// neither route protected either workspace.</para>
    ///
    /// <para>An entry that is not a rooted path is no answer, as a whole line is for the other two
    /// variables, and a list with no usable entry falls back to the documented default.</para>
    /// </summary>
    private string[] Workspaces(string[] lines)
    {
        string[] listed = lines.Length > 2
            ?
            [
                .. lines[2]
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(Path.IsPathRooted),
            ]
            : [];

        return listed.Length > 0 ? listed : [DefaultGoPath];
    }

    /// <summary>Where Go keeps things, and whether Go itself is what said so.</summary>
    /// <param name="Answered">
    /// False when <c>go env</c> did not run, or answered with nothing usable, in which case these
    /// are Deguffer's documented defaults rather than this machine's configuration. The plan says
    /// which of the two it is holding, because "Go reports its build cache as X" is a claim about a
    /// subprocess that may never have spoken — and if it did not, X is a guess that a machine with a
    /// moved cache will not match.
    /// </param>
    /// <param name="GoPaths">Every workspace <c>GOPATH</c> lists, in Go's order. Never empty.</param>
    private sealed record GoLocations(
        string BuildCache,
        string ModuleCache,
        IReadOnlyList<string> GoPaths,
        bool Answered);

    private static string? Reported(string[] lines, int index) =>
        index < lines.Length && Path.IsPathRooted(lines[index]) ? lines[index] : null;
}
