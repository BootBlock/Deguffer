using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The uv (Python) package cache (~3.5 GB on the audited machine).
///
/// §5.1: uv has an official eviction command, so the plan calls it rather than deleting the cache
/// directory. That matters more here than it does for npm, because <c>%LOCALAPPDATA%\uv</c> is not
/// a cache directory — it is uv's whole state directory, and the tools and managed Python
/// interpreters uv installs live beside <c>cache</c> under it. Deleting the root to reclaim the
/// cache would uninstall them.
/// </summary>
public sealed class UvCacheProvider : CleanupProviderBase
{
    private string? _resolvedCacheDirectory;

    public UvCacheProvider(
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

    public override string Id => "uv";

    public override string Name => "uv package cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The next uv install re-downloads and re-unpacks packages. Existing virtual environments " +
        "and uv-installed tools are untouched.";

    protected override IReadOnlyList<string> ConflictingProcessNames => ["uv", "uvx"];

    /// <summary>
    /// Where uv keeps its cache when it has not been asked. The location moves with
    /// <c>UV_CACHE_DIR</c>, <c>--cache-dir</c> and <c>cache-dir</c> in <c>uv.toml</c>, so this is a
    /// last resort rather than an assumption.
    /// </summary>
    public string DefaultCacheDirectory => Path.Combine(Environment.LocalAppData, "uv", "cache");

    /// <summary>uv's state directory. Exposed so tests can assert it is never targeted (§5.2).</summary>
    public string StateRoot => Path.Combine(Environment.LocalAppData, "uv");

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("uv") is not null);

    /// <summary>
    /// The resolved cache directory is a cache like any other, and UV_CACHE_DIR can move it
    /// between one scan and the next. Keeping it across an invalidation would measure a location
    /// uv has stopped using.
    /// </summary>
    public override void InvalidateCaches()
    {
        _resolvedCacheDirectory = null;
        base.InvalidateCaches();
    }

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        var uv = Environment.FindExecutable("uv");
        if (uv is null)
        {
            return EmptyPlan("uv is not installed on this machine.");
        }

        var cacheDirectory = await ResolveCacheDirectoryAsync(uv, ct).ConfigureAwait(false);

        if (!LongPath.DirectoryExists(cacheDirectory))
        {
            return EmptyPlan($"uv is installed but its cache directory does not exist yet ({cacheDirectory}).");
        }

        var measured = await MeasureAllAsync([cacheDirectory], ct).ConfigureAwait(false);
        var notes = new List<PlanNote>();

        // Deliberately not --force: that flag tells uv to ignore its own in-use checks, and §5.3
        // prefers warning the user that something is running over overriding the tool's judgement.
        var steps = new List<CleanupStep>
        {
            new RunCommandStep(uv, "cache clean", "Clear the uv cache using uv's own command")
            {
                Estimated = measured.Total,
                MeasuredPaths = [cacheDirectory],
            },
        };

        notes.Add(new PlanNote(
            PlanNoteSeverity.Information,
            $"uv reports its cache directory as {cacheDirectory}."));

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
            ProtectedPaths = BuildProtectedPaths(),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    /// <summary>
    /// §5.6. The siblings of <c>cache</c> are the point: <c>tools</c> holds CLI tools the user
    /// installed with <c>uv tool install</c> and <c>python</c> holds downloaded interpreters, both
    /// of which look like cache and are not. The cache directory itself is not protected — uv's own
    /// command removes it rather than emptying it, and recreates it on next use.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths() => Protect(
        (StateRoot, "uv's state directory must survive — only the cache within it is cleared."),
        (Path.Combine(StateRoot, "tools"), "CLI tools installed with 'uv tool install'."),
        (Path.Combine(StateRoot, "python"), "Python interpreters uv downloaded and manages."));

    private async Task<string> ResolveCacheDirectoryAsync(string uv, CancellationToken ct)
    {
        if (_resolvedCacheDirectory is not null)
        {
            return _resolvedCacheDirectory;
        }

        // --color never because uv colourises this path when it thinks it is writing to a terminal,
        // and the escape sequences would land inside the parsed path.
        var outcome = await Runner.RunAsync(uv, "cache dir --color never", ct).ConfigureAwait(false);

        var reported = outcome.Succeeded
            ? outcome.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(Path.IsPathRooted)
            : null;

        return _resolvedCacheDirectory = reported ?? DefaultCacheDirectory;
    }
}
