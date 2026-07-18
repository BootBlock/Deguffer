using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The npm content-addressable cache (~2.4 GB on the audited machine).
///
/// §5.1: npm has an official eviction command, so the plan calls it rather than deleting the
/// cache directory. The directory is measured only to report a size and to attribute what the
/// command reclaimed.
/// </summary>
public sealed class NpmCacheProvider : CleanupProviderBase
{
    private string? _resolvedCacheDirectory;

    public NpmCacheProvider(
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

    public override string Id => "npm";

    public override string Name => "npm package cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The next npm install re-downloads packages from the registry. Installed node_modules are untouched.";

    protected override IReadOnlyList<string> ConflictingProcessNames => ["node", "npm"];

    /// <summary>
    /// Where npm keeps its cache. Resolved by asking npm itself, falling back to the documented
    /// Windows default — the location is configurable, so it is never simply assumed.
    /// </summary>
    public string DefaultCacheDirectory => Path.Combine(Environment.LocalAppData, "npm-cache");

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("npm") is not null);

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        var npm = Environment.FindExecutable("npm");
        if (npm is null)
        {
            return EmptyPlan("npm is not installed on this machine.");
        }

        var cacheDirectory = await ResolveCacheDirectoryAsync(npm, ct).ConfigureAwait(false);
        var notes = new List<PlanNote>();
        var steps = new List<CleanupStep>();

        if (!LongPath.DirectoryExists(cacheDirectory))
        {
            return EmptyPlan($"npm is installed but its cache directory does not exist yet ({cacheDirectory}).");
        }

        var measured = await MeasureAllAsync([cacheDirectory], ct).ConfigureAwait(false);

        steps.Add(new RunCommandStep(npm, "cache clean --force", "Clear the npm cache using npm's own command")
        {
            Estimated = measured.Total,
            MeasuredPaths = [cacheDirectory],
        });

        notes.Add(new PlanNote(
            PlanNoteSeverity.Information,
            $"npm reports its cache directory as {cacheDirectory}."));

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
    /// §5.6. <c>.npmrc</c> holds registry auth tokens, and <c>%APPDATA%\npm</c> holds globally
    /// installed tools — neither is cache, and both sit close enough to be worth proving.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths() => Protect(
        (Path.Combine(Environment.UserProfile, ".npmrc"), "User npm configuration, which may hold registry auth tokens."),
        (Path.Combine(Environment.RoamingAppData, "npm"), "Globally installed npm packages and their shims."));

    private async Task<string> ResolveCacheDirectoryAsync(string npm, CancellationToken ct)
    {
        if (_resolvedCacheDirectory is not null)
        {
            return _resolvedCacheDirectory;
        }

        var outcome = await Runner.RunAsync(npm, "config get cache", ct).ConfigureAwait(false);

        var reported = outcome.Succeeded
            ? outcome.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(line => Path.IsPathRooted(line))
            : null;

        return _resolvedCacheDirectory = reported ?? DefaultCacheDirectory;
    }
}
