using System.Text.Json;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// PlatformIO's download cache — the first Tier 2 source, and the one that proves the §7
/// confirmation seam against a real subject.
///
/// §5.1: PlatformIO ships its own eviction command, and here that is not merely preferred but
/// load-bearing. <c>%USERPROFILE%\.platformio</c> is the tool's whole core directory: the installed
/// toolchains under <c>packages</c> are gigabytes and dominate its size, while the genuinely
/// disposable cache is a fraction of that. A provider reasoning from size, or from the plausible
/// reading that a directory named <c>packages</c> is a package cache, would be wrong by orders of
/// magnitude in the destructive direction. <c>pio system prune --cache</c> knows the difference.
///
/// Tier 2 rather than Tier 1 because restoring the cache means re-downloading it, and embedded
/// toolchains are commonly fetched over connections where that is a real cost.
/// </summary>
public sealed class PlatformIoCacheProvider : CleanupProviderBase
{
    private string? _resolvedCacheDirectory;

    public PlatformIoCacheProvider(
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

    public override string Id => "platformio";

    public override string Name => "PlatformIO download cache";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next build re-downloads cached package archives and registry responses. Installed " +
        "platforms, toolchains and your global libraries are untouched.";

    protected override IReadOnlyList<string> ConflictingProcessNames => ["pio", "platformio"];

    /// <summary>
    /// PlatformIO's core directory when it has not been asked. <c>PLATFORMIO_CORE_DIR</c> moves it,
    /// and on Windows a <c>.platformio</c> at the root of the profile's drive wins over this one, so
    /// this is a last resort rather than an assumption.
    ///
    /// Also what the §5.2 protected paths are built from, and what tests assert is never targeted.
    /// </summary>
    public string CoreRoot => Path.Combine(Environment.UserProfile, ".platformio");

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("pio") is not null);

    /// <summary>
    /// <c>PLATFORMIO_CORE_DIR</c> and a project's <c>cache_dir</c> setting can both move this
    /// between one scan and the next, so a remembered answer would measure a location PlatformIO
    /// has stopped using.
    /// </summary>
    public override void InvalidateCaches()
    {
        _resolvedCacheDirectory = null;
        base.InvalidateCaches();
    }

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        var pio = Environment.FindExecutable("pio");
        if (pio is null)
        {
            return EmptyPlan("PlatformIO is not installed on this machine.");
        }

        var cacheDirectory = await ResolveCacheDirectoryAsync(pio, ct).ConfigureAwait(false);

        if (!LongPath.DirectoryExists(cacheDirectory))
        {
            return EmptyPlan(
                $"PlatformIO is installed but its cache directory does not exist yet ({cacheDirectory}).");
        }

        var measured = await MeasureAllAsync([cacheDirectory], ct).ConfigureAwait(false);

        // --cache scopes prune to the cache alone. Without it, prune also removes "unnecessary"
        // core and platform packages — a judgement about installed toolchains that belongs to the
        // user, not to this tool. --force answers prune's own interactive prompt, which is not the
        // same thing as overriding a safety check: §7's confirmation has already been satisfied by
        // the time this runs.
        var steps = new List<CleanupStep>
        {
            new RunCommandStep(pio, "system prune --cache --force", "Clear the PlatformIO cache using its own command")
            {
                Estimated = measured.Total,
                MeasuredPaths = [cacheDirectory],
            },
        };

        var notes = new List<PlanNote>
        {
            new(PlanNoteSeverity.Information, $"PlatformIO reports its cache directory as {cacheDirectory}."),
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
    /// §5.6. The siblings are the whole point here: <c>packages</c> and <c>platforms</c> are the
    /// installed toolchains that make up nearly all of the core directory's size, <c>penv</c> and
    /// <c>python3</c> are the interpreter PlatformIO itself runs on, and <c>lib</c> holds globally
    /// installed user libraries that were never a cache at all.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths() => Protect(
        (CoreRoot, "PlatformIO's core directory must survive — only the cache within it is cleared."),
        (Path.Combine(CoreRoot, "packages"), "Installed toolchains and frameworks; gigabytes to re-download."),
        (Path.Combine(CoreRoot, "platforms"), "Installed development platform definitions."),
        (Path.Combine(CoreRoot, "penv"), "The virtual environment PlatformIO Core itself runs in."),
        (Path.Combine(CoreRoot, "python3"), "The bundled Python interpreter backing that environment."),
        (Path.Combine(CoreRoot, "lib"), "Globally installed user libraries — never a cache."));

    /// <summary>
    /// Ask PlatformIO where its cache is. <c>--json-output</c> rather than scraping the human
    /// listing: the field names are part of a documented machine-readable contract, the alignment
    /// of the text table is not.
    /// </summary>
    private async Task<string> ResolveCacheDirectoryAsync(string pio, CancellationToken ct)
    {
        if (_resolvedCacheDirectory is not null)
        {
            return _resolvedCacheDirectory;
        }

        var outcome = await Runner.RunAsync(pio, "system info --json-output", ct).ConfigureAwait(false);

        return _resolvedCacheDirectory =
            (outcome.Succeeded ? ReadCacheDirectory(outcome.StandardOutput) : null)
            ?? Path.Combine(CoreRoot, ".cache");
    }

    /// <summary>
    /// <c>cache_dir</c> is the answer when PlatformIO reports it; otherwise the cache is the
    /// <c>.cache</c> child of whichever core directory it named, which still beats assuming the
    /// default profile location.
    /// </summary>
    private static string? ReadCacheDirectory(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (ReadPath(document.RootElement, "cache_dir") is { } cache)
            {
                return cache;
            }

            return ReadPath(document.RootElement, "core_dir") is { } core
                ? Path.Combine(core, ".cache")
                : null;
        }
        catch (JsonException)
        {
            // An older PlatformIO that does not understand --json-output prints its usage text to
            // stdout and still exits zero, so malformed output here is an expected outcome rather
            // than a broken install. The default location is the honest fallback.
            return null;
        }
    }

    /// <summary>
    /// PlatformIO wraps each value in <c>{"value": …, "default": …}</c> in some versions and emits
    /// a bare string in others, so both shapes are read rather than assuming the current one.
    /// </summary>
    private static string? ReadPath(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property))
        {
            return null;
        }

        var value = property.ValueKind == JsonValueKind.Object
            && property.TryGetProperty("value", out var wrapped)
                ? wrapped
                : property;

        return value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } path
            && Path.IsPathRooted(path)
                ? path
                : null;
    }
}
