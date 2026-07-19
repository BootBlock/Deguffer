using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The pip (Python) HTTP and wheel cache.
///
/// §5.1: pip has an official eviction command, so the plan calls <c>pip cache purge</c> rather than
/// deleting paths. Unlike uv, the location pip reports <em>is</em> a cache directory rather than a
/// state directory with tools inside it — pip keeps no installed packages there, because those go
/// into the environment's <c>site-packages</c>. That is why this provider protects the environment
/// rather than a sibling of the cache: the failure to guard against is a user reading "Python cache"
/// and expecting their virtual environments to be at risk.
///
/// The cache mixes two kinds of entry. <c>http</c>/<c>http-v2</c> hold downloaded artefacts, which
/// cost a re-download. <c>wheels</c> holds wheels pip <em>built locally</em> from source
/// distributions, which cost a rebuild — and for a package with C extensions that is compilation,
/// not a download. Both are still regenerable without user input, so this stays Tier 1, but
/// <see cref="WhatHappensOnNextUse"/> says so rather than promising a uniformly cheap refill.
/// </summary>
public sealed class PipCacheProvider : CleanupProviderBase
{
    /// <summary>
    /// The commands pip may be installed as. Order matters only in that the first one found wins;
    /// both spellings drive the same pip, and asking either for its cache directory is equivalent.
    /// </summary>
    private static readonly string[] CommandNames = ["pip", "pip3"];

    private string? _resolvedCacheDirectory;

    public PipCacheProvider(
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

    public override string Id => "pip";

    public override string Name => "pip package cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The next pip install re-downloads packages, and rebuilds any wheel pip had previously " +
        "built from source — which for a package with C extensions means compiling it again. " +
        "Installed packages, virtual environments and site-packages are untouched.";

    protected override IReadOnlyList<string> ConflictingProcessNames => ["pip", "python", "python3"];

    /// <summary>
    /// Where pip keeps its cache when it has not been asked. The location moves with
    /// <c>PIP_CACHE_DIR</c>, <c>--cache-dir</c> and the <c>cache-dir</c> key in <c>pip.ini</c>, so
    /// this is a last resort rather than an assumption.
    /// </summary>
    public string DefaultCacheDirectory => Path.Combine(Environment.LocalAppData, "pip", "Cache");

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(FindPip() is not null);

    /// <summary>
    /// PIP_CACHE_DIR can move the cache between one scan and the next, so a remembered location
    /// would measure a directory pip has stopped using.
    /// </summary>
    public override void InvalidateCaches()
    {
        _resolvedCacheDirectory = null;
        base.InvalidateCaches();
    }

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        if (FindPip() is not { } pip)
        {
            return EmptyPlan("pip is not installed on this machine.");
        }

        var cacheDirectory = await ResolveCacheDirectoryAsync(pip, ct).ConfigureAwait(false);

        if (!LongPath.DirectoryExists(cacheDirectory))
        {
            return EmptyPlan($"pip is installed but its cache directory does not exist yet ({cacheDirectory}).");
        }

        var measured = await MeasureAllAsync([cacheDirectory], ct).ConfigureAwait(false);

        var steps = new List<CleanupStep>
        {
            new RunCommandStep(pip, "cache purge", "Clear the pip cache using pip's own command")
            {
                Estimated = measured.Total,
                MeasuredPaths = [cacheDirectory],
            },
        };

        var notes = new List<PlanNote>
        {
            new(PlanNoteSeverity.Information, $"pip reports its cache directory as {cacheDirectory}."),
        };

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
    /// §5.6. <c>pip.ini</c> is the one thing under <c>%LOCALAPPDATA%\pip</c> that is not cache —
    /// it holds index URLs and may carry credentials for a private package index — and it sits in
    /// the parent of the cache directory pip reports. Naming the parent as well is what stops a
    /// future change from reclaiming the cache by removing the directory that contains it.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths()
    {
        var localRoot = Path.Combine(Environment.LocalAppData, "pip");

        // pip reads pip.ini from both the local and roaming profile directories, and either may be
        // the one that exists. Naming both keeps §5.6's list an honest statement of what survived
        // rather than a guess at which copy this machine happens to use.
        return Protect(
            (localRoot, "pip's directory must survive — only the cache within it is cleared."),
            (Path.Combine(localRoot, "pip.ini"), "pip configuration, which may hold private index URLs and credentials."),
            (Path.Combine(Environment.RoamingAppData, "pip", "pip.ini"), "Roaming pip configuration, which may hold private index URLs and credentials."));
    }

    private string? FindPip()
    {
        foreach (var name in CommandNames)
        {
            if (Environment.FindExecutable(name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private async Task<string> ResolveCacheDirectoryAsync(string pip, CancellationToken ct)
    {
        if (_resolvedCacheDirectory is not null)
        {
            return _resolvedCacheDirectory;
        }

        // --no-color because pip colourises output when it believes it is writing to a terminal,
        // and the escape sequences would land inside the parsed path.
        var outcome = await Runner.RunAsync(pip, "cache dir --no-color", ct).ConfigureAwait(false);

        var reported = outcome.Succeeded
            ? outcome.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(Path.IsPathRooted)
            : null;

        return _resolvedCacheDirectory = reported ?? DefaultCacheDirectory;
    }
}
