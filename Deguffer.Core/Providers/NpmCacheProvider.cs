using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The npm content-addressable cache (~2.4 GB on the audited machine).
///
/// §5.1: npm has an official eviction command, so the plan calls it rather than deleting the
/// cache directory. What the command removes is measured only to report a size and to attribute
/// what it reclaimed.
///
/// <para><c>npm cache clean</c> removes <c>_cacache</c> inside the cache directory and nothing else.
/// The same directory holds <c>_npx</c>, the packages <c>npx</c> ran, <c>_logs</c> and more, and
/// the command leaves them all, so measuring the whole directory would offer bytes no run frees.</para>
/// </summary>
public sealed class NpmCacheProvider : CleanupProviderBase
{
    /// <summary>The folder in the cache directory that <c>npm cache clean</c> removes.</summary>
    private const string ContentCacheName = "_cacache";

    /// <summary>Where npx keeps the packages it ran. No npm command evicts it.</summary>
    private const string NpxCacheName = "_npx";

    private readonly PassMemo<Task<AskedLocation>> _resolvedCacheDirectory = new();

    public NpmCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner)
    {
    }

    public override string Id => "npm";

    public override string Name => "npm package cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next npm install re-downloads packages from the registry. Installed node_modules are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "npm, the package manager bundled with Node.js",
        Publisher = "npm, Inc., part of GitHub",
        Purpose = "npm keeps a copy of every package archive it downloads in one cache shared by "
            + "all your projects, so installing the same version again is served from disk instead "
            + "of from the registry.",
        Recommendation = "Deguffer runs npm's own cache clean, and the cache refills from the "
            + "registry as installs need it. Anything already installed into a project is "
            + "untouched.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["node", "npm"];

    /// <summary>
    /// Where npm keeps its cache. Resolved by asking npm itself, falling back to the documented
    /// Windows default — the location is configurable, so it is never simply assumed.
    /// </summary>
    public string DefaultCacheDirectory => Path.Combine(Environment.LocalAppData, "npm-cache");

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside. npm has no root to declare: the cache directory
    /// comes from <c>npm config get cache</c>, and the documented default is a cache folder rather
    /// than one with configuration beside it. What it does have is a loose file, and a file is a root
    /// nothing is ever below — refused by the equality branch, and by nothing else.
    ///
    /// <para>The global packages folder is declared too, recognising nothing, because the plan
    /// names it as a path that must survive and §7.1 has Explore refuse every such path.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        new ToolRoot(
            Path.Combine(Environment.UserProfile, ".npmrc"),
            "This is your npm configuration, and it may hold registry authentication tokens. "
            + "Deguffer never removes it.",
            static _ => false),

        new ToolRoot(
            Path.Combine(Environment.RoamingAppData, "npm"),
            "This is where npm keeps the packages you installed globally, and the commands that run "
            + "them. Deguffer never removes anything here.",
            static _ => false),
    ];

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("npm") is not null);

    /// <summary>
    /// What <c>npm cache clean</c> removes: <c>_cacache</c> in the cache npm reports, and in the
    /// documented default as well, because configuration can move the cache back to it after a
    /// duplicate search has asked.
    /// </summary>
    public override async Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default)
    {
        var npm = Environment.FindExecutable("npm");

        if (npm is null)
        {
            return [CleanedPlace.Whole(ContentCacheIn(DefaultCacheDirectory))];
        }

        var reported = await ResolveCacheDirectoryAsync(npm, ct).ConfigureAwait(false);

        return [CleanedPlace.Whole(ContentCacheIn(DefaultCacheDirectory)), CleanedPlace.Whole(ContentCacheIn(reported.Path))];
    }

    /// <summary>
    /// npm's cache location is configuration, not a constant — <c>npm config set cache</c> and the
    /// <c>npm_config_cache</c> environment variable both move it. Keeping the resolved answer across
    /// an invalidation would measure a directory npm has stopped using.
    /// </summary>
    public override void InvalidateCaches()
    {
        _resolvedCacheDirectory.Forget();
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var npm = Environment.FindExecutable("npm");
        if (npm is null)
        {
            return EmptyPlan("npm is not installed on this machine.");
        }

        var located = await ResolveCacheDirectoryAsync(npm, ct).ConfigureAwait(false);
        var cacheDirectory = located.Path;
        var notes = new List<PlanNote>();
        var steps = new List<CleanupStep>();

        if (NothingToPlanFor(cacheDirectory, located.DescribeAbsent("npm", "cache directory")) is { } nothing)
        {
            return nothing;
        }

        var contentCache = ContentCacheIn(cacheDirectory);

        if (NothingToPlanFor(
                contentCache,
                $"npm's cache holds nothing its clean command removes ({contentCache} does not exist).") is { } clear)
        {
            return clear;
        }

        var measured = await MeasureAllAsync([contentCache], ct).ConfigureAwait(false);

        // --force overrides no safety check. Since npm 5, cache clean refuses to run without it and
        // points at npm cache verify instead, because the cache verifies itself. Freeing the space is
        // what the user asked for, so the refusal is answered rather than obeyed.
        steps.Add(new RunCommandStep(npm, "cache clean --force", "Clear the npm cache using npm's own command")
        {
            // npm's cache checks every entry against its hash as it reads it, so a clean ended part-way
            // leaves misses, never a wrong package.
            WhenCancelled = CancelledCommand.Ended,
            Estimated = measured.Total,
            MeasuredPaths = [contentCache],
        });

        notes.Add(new PlanNote(
            PlanNoteSeverity.Information,
            located.Describe("npm", "cache directory") + $" Its clean command removes {ContentCacheName} in "
            + "npm's cache directory, and leaves everything else, such as the packages npx ran and npm's logs."));

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
            ProtectedPaths = await BuildProtectedPathsAsync(cacheDirectory, ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    /// <summary>
    /// §5.6. <c>.npmrc</c> holds registry auth tokens, and <c>%APPDATA%\npm</c> holds globally
    /// installed tools — neither is cache, and both sit close enough to be worth proving. The cache
    /// directory and its <c>_npx</c> sit beside what the command removes, and both must outlive it.
    /// </summary>
    private Task<IReadOnlyList<ProtectedPath>> BuildProtectedPathsAsync(string cacheDirectory, CancellationToken ct) => ProtectAsync(
        [
            (Path.Combine(Environment.UserProfile, ".npmrc"), "User npm configuration, which may hold registry auth tokens."),
            (Path.Combine(Environment.RoamingAppData, "npm"), "Globally installed npm packages and their shims."),
            (cacheDirectory, "npm's cache directory must survive — only the package cache within it is cleared."),
            (Path.Combine(cacheDirectory, NpxCacheName), "Packages npx ran, which npm's clean command leaves."),
        ],
        ct);

    private static string ContentCacheIn(string cacheDirectory) => Path.Combine(cacheDirectory, ContentCacheName);

    private Task<AskedLocation> ResolveCacheDirectoryAsync(string npm, CancellationToken ct) =>
        _resolvedCacheDirectory.Get(() => AskCacheDirectoryAsync(npm, ct));

    private async Task<AskedLocation> AskCacheDirectoryAsync(string npm, CancellationToken ct)
    {
        var outcome = await Runner.RunAsync(npm, "config get cache", ToolRun.Query, ct).ConfigureAwait(false);

        return AskedLocation.FromLastRootedLine(outcome, DefaultCacheDirectory);
    }
}
