using System.Text.Json;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// PlatformIO's download cache, and the installed packages PlatformIO itself reports that nothing
/// still needs. The first Tier 2 source, and the one that proves the §7 confirmation seam against a
/// real subject.
///
/// §5.1: PlatformIO ships its own eviction command, and here that is not merely preferred but
/// load-bearing. <c>%USERPROFILE%\.platformio</c> is the tool's whole core directory: the installed
/// toolchains under <c>packages</c> are gigabytes and dominate its size, while the genuinely
/// disposable cache is a fraction of that. A provider reasoning from size, or from the plausible
/// reading that a directory named <c>packages</c> is a package cache, would be wrong by orders of
/// magnitude in the destructive direction.
///
/// <para><b>Which is why the second offer is PlatformIO's answer rather than ours.</b> On the
/// surveyed machine the cache measured 0.9 MB against a 5.7 GB core directory, and a provider
/// reaching 0.02% of its own folder looks like a defect. It was not one: <c>pio system prune
/// --dry-run</c> put the reclaimable packages at zero, because two <c>espressif32</c> platform
/// versions were installed and PlatformIO collects the required tool packages of every installed
/// platform. That zero is a property of one configuration rather than of PlatformIO. A machine that
/// upgraded a platform in place has superseded toolchains nothing collects, hundreds of megabytes
/// each, and that is where the gigabytes in this directory actually are. So the tool is asked for
/// the whole figure: where it names something, a second step offers it with PlatformIO's own list as
/// the evidence, and where it names nothing, no row appears at all.</para>
///
/// <para>Tier 2 rather than Tier 1 because restoring either is a download, and embedded toolchains
/// are commonly fetched over connections where that is a real cost.</para>
/// </summary>
public sealed class PlatformIoCacheProvider : CleanupProviderBase
{
    /// <summary>
    /// <c>--cache</c> scopes prune to the cache alone, leaving the packages to the step below, which
    /// says in its own words what it is doing. Without the flag one command would do both under one
    /// description, and the user would be agreeing to the toolchains by agreeing to the cache.
    /// </summary>
    private const string CachePrune = "system prune --cache";

    /// <summary>
    /// The two package categories, which are one offer because they answer one question: what has
    /// PlatformIO installed that nothing installed still refers to? Core packages are superseded
    /// versions of PlatformIO's own dependencies, platform packages are tool packages no installed
    /// development platform requires, and both live in the same <c>packages</c> directory.
    ///
    /// <para><c>--dry-run</c> is read-only, and that was established from PlatformIO's source rather
    /// than assumed: in <c>platformio/system/prune.py</c>, <c>prune_cached_data</c> guards its delete
    /// with <c>if not dry_run:</c>, and both package handlers return their candidate list before
    /// reaching the uninstall loop. <c>--force</c> is deliberately not passed beside it. The dry run
    /// needs no prompt suppressed, and a command that would still stop and ask is the one that fails
    /// safely if <c>--dry-run</c> ever goes missing from this string.</para>
    /// </summary>
    private const string PackagePrune = "system prune --core-packages --platform-packages";

    /// <summary>How many packages the evidence note names before it summarises the rest.</summary>
    private const int PackagesNamed = 5;

    private PlatformIoLocations? _locations;

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

    public override string Name => "PlatformIO cache and unused packages";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next build re-downloads cached package archives and registry responses. Where "
        + "PlatformIO also named packages that nothing installed still refers to, the next build "
        + "wanting one downloads it again, and a toolchain is a multi-gigabyte download. Your "
        + "installed platforms, every toolchain they require and your global libraries are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "PlatformIO, a build system for embedded development",
        Publisher = "PlatformIO Labs",
        Purpose = "PlatformIO caches the package archives it downloads and the registry responses "
            + "it has already received, inside the same core directory that holds your installed "
            + "toolchains and libraries. That directory also accumulates superseded toolchains, "
            + "which stay behind when a development platform is upgraded in place.",
        Recommendation = "Embedded toolchains are often fetched over connections where a "
            + "re-download is a real cost. Deguffer runs PlatformIO's own prune command, and asks "
            + "that same command which installed packages nothing still refers to rather than "
            + "deciding for itself.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["pio", "platformio"];

    /// <summary>
    /// PlatformIO's core directory when it has not been asked. <c>PLATFORMIO_CORE_DIR</c> moves it,
    /// and on Windows a <c>.platformio</c> at the root of the profile's drive wins over this one, so
    /// this is a last resort rather than an assumption.
    /// </summary>
    public string CoreRoot => Path.Combine(Environment.UserProfile, ".platformio");

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside, and this is the provider that needs it most: the
    /// installed toolchains under <c>packages</c> dominate the core directory's size, so a size
    /// picture puts them in front of the user before anything else in there.
    ///
    /// <para><c>.cache</c> stays the only recognised child, and <c>packages</c> deliberately is not,
    /// even though a plan may now clear part of it. What this predicate grants is Explore's offer of
    /// a whole child directory, and the whole of <c>packages</c> is exactly the multi-gigabyte
    /// mistake this provider exists to avoid. Which packages inside it are unreferenced is
    /// PlatformIO's judgement, arrived at by running PlatformIO, and no name-shaped rule reproduces
    /// it.</para>
    ///
    /// <para>Built from <see cref="CoreRoot"/> rather than from the directory PlatformIO reports,
    /// because Explore consults this on every path it draws and it has to answer without starting a
    /// subprocess. A relocated core directory is therefore not covered here — the same trade-off
    /// <see cref="CondaCacheProvider"/> states, and it fails safe, because a directory no provider
    /// recognises is Tier 4.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        new ToolRoot(
            CoreRoot,
            "This is PlatformIO's own folder. Deguffer clears the download cache inside it, and "
            + "removes an installed package only where PlatformIO itself reports that nothing needs "
            + "it, because the toolchains, the Python that PlatformIO runs on and your global "
            + "libraries all sit beside that cache.",
            static name => name.Equals(".cache", StringComparison.OrdinalIgnoreCase)),
    ];

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("pio") is not null);

    /// <summary>
    /// <c>PLATFORMIO_CORE_DIR</c> and a project's <c>cache_dir</c> setting can both move these
    /// between one scan and the next, so a remembered answer would measure a location PlatformIO
    /// has stopped using.
    /// </summary>
    public override void InvalidateCaches()
    {
        _locations = null;
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var pio = Environment.FindExecutable("pio");
        if (pio is null)
        {
            return EmptyPlan("PlatformIO is not installed on this machine.");
        }

        var locations = await ResolveLocationsAsync(pio, ct).ConfigureAwait(false);

        var cache = await PlanCacheAsync(pio, locations, ct).ConfigureAwait(false);
        var packages = await PlanUnusedPackagesAsync(pio, locations, ct).ConfigureAwait(false);

        var steps = new List<CleanupStep>(2);
        if (cache.Step is { } cacheStep)
        {
            steps.Add(cacheStep);
        }

        if (packages.Step is { } packageStep)
        {
            steps.Add(packageStep);
        }

        var notes = new List<PlanNote>(cache.Notes);
        notes.AddRange(packages.Notes);

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
            ProtectedPaths = BuildProtectedPaths(locations),
            Notes = notes,
            // Two measurements now, and the first reason to appear is the one the user is shown —
            // the same rule ScanBatch already applies within a single measurement.
            Fallback = cache.Fallback != FallbackReason.None ? cache.Fallback : packages.Fallback,
        };
    }

    /// <summary>
    /// §5.1's cache step, measured by Deguffer rather than taken from the tool. PlatformIO's own
    /// figure for the cache matched this measurement exactly on the surveyed machine, and this one
    /// is exact bytes rather than a humanised two decimal places — which matters, because the
    /// executor subtracts a re-measurement of the same directory from it to report what was
    /// actually reclaimed.
    /// </summary>
    private async Task<PlanPart> PlanCacheAsync(
        string pio,
        PlatformIoLocations locations,
        CancellationToken ct)
    {
        if (!LongPath.DirectoryExists(locations.CacheDirectory))
        {
            return new PlanPart(
                null,
                [Information(
                    $"PlatformIO's cache directory does not exist yet ({locations.CacheDirectory}).")],
                FallbackReason.None);
        }

        var measured = await MeasureAllAsync([locations.CacheDirectory], ct).ConfigureAwait(false);

        var notes = new List<PlanNote>
        {
            Information($"PlatformIO reports its cache directory as {locations.CacheDirectory}."),
        };

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        return new PlanPart(
            new RunCommandStep(
                pio,
                // --force answers prune's own interactive prompt, which is not the same thing as
                // overriding a safety check: §7's confirmation has already been satisfied by the
                // time this runs.
                CachePrune + " --force",
                "Clear the PlatformIO cache using its own command")
            {
                Estimated = measured.Total,
                MeasuredPaths = [locations.CacheDirectory],
            },
            notes,
            measured.Fallback);
    }

    /// <summary>
    /// §5.1 taken as far as it goes: PlatformIO is asked what its own prune would remove from the
    /// packages it installed, and its answer is the whole of the offer. Where it names nothing, the
    /// packages directory is not even measured and no row appears for it.
    ///
    /// <para><b>The figure is the tool's, and it has to be.</b> Measuring <c>packages</c> instead
    /// would count every toolchain an installed platform still requires, which on the surveyed
    /// machine was all 5,670.9 MB of it against a true reclaim of zero. That is the §5.4 over-report
    /// in its most dangerous form, because the number is enormous and reads like a find. So the step
    /// carries PlatformIO's estimate and Deguffer's own probe of the same directory as separate
    /// figures: the probe is the "before" that the executor subtracts its re-measurement from, and
    /// subtracting from the estimate instead would report a reclaim of minus five gigabytes.</para>
    /// </summary>
    private async Task<PlanPart> PlanUnusedPackagesAsync(
        string pio,
        PlatformIoLocations locations,
        CancellationToken ct)
    {
        var outcome = await Runner
            .RunAsync(pio, PackagePrune + " --dry-run", ct)
            .ConfigureAwait(false);

        var preview = PlatformIoPruneReport.TryRead(outcome.StandardOutput);

        if (preview is null)
        {
            return new PlanPart(
                null,
                [Information(
                    "PlatformIO did not report what its own prune would remove from its installed "
                    + "packages, so only the cache is offered. Deciding here which toolchains are "
                    + "unreferenced is the judgement §5.2 leaves with the tool that installed them.")],
                FallbackReason.None);
        }

        var packages = LongPath.Display(locations.PackagesDirectory);

        if (preview.Bytes == 0)
        {
            return new PlanPart(
                null,
                [Information(
                    $"PlatformIO reports nothing unnecessary in {packages}, so none of it is "
                    + "offered. Every toolchain there is still required by an installed platform, "
                    + "or is the current version of a core package.")],
                FallbackReason.None);
        }

        var probed = await MeasureAllAsync([locations.PackagesDirectory], ct).ConfigureAwait(false);

        var notes = new List<PlanNote>
        {
            Information(
                $"The package figure is PlatformIO's own dry run, not a measurement of {packages}. "
                + "Anything an installed platform still requires is neither counted nor removed."),
        };

        if (DescribePackages(preview.Packages) is { } evidence)
        {
            notes.Add(Information(evidence));
        }

        if (probed.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        return new PlanPart(
            new RunCommandStep(
                pio,
                PackagePrune + " --force",
                "Remove packages no installed platform still needs, using PlatformIO's own command")
            {
                // Approximate, and not for want of trying: PlatformIO prints its total humanised to
                // two decimal places, so it is rounded before Deguffer ever sees it.
                Estimated = ScanSize.Approximate(preview.Bytes),
                MeasuredPaths = [locations.PackagesDirectory],
                MeasuredBefore = probed.Total,
            },
            notes,
            probed.Fallback);
    }

    /// <summary>
    /// PlatformIO's own list, largest first, as the evidence behind the row. A user who is about to
    /// agree to a multi-gigabyte toolchain removal can check a named package against their own
    /// projects; a bare total gives them nothing to check.
    ///
    /// <para>Null where the report carried a total but no readable table. The estimate note beside
    /// this one always appears, so a row is never left with no explanation of where its figure came
    /// from.</para>
    /// </summary>
    private static string? DescribePackages(IReadOnlyList<PlatformIoPrunablePackage> packages)
    {
        if (packages.Count == 0)
        {
            return null;
        }

        var named = string.Join(
            ", ",
            packages.Take(PackagesNamed).Select(package => $"{package.Name} ({package.Size})"));

        var rest = packages.Count - PackagesNamed;

        return rest > 0
            ? $"PlatformIO named {packages.Count} packages, largest first: {named}, and {rest} more."
            : $"PlatformIO named these packages: {named}.";
    }

    /// <summary>
    /// §5.6. The siblings are the whole point here: <c>packages</c> and <c>platforms</c> are the
    /// installed toolchains that make up nearly all of the core directory's size, <c>penv</c> and
    /// <c>python3</c> are the interpreter PlatformIO itself runs on, and <c>lib</c> holds globally
    /// installed user libraries that were never a cache at all.
    ///
    /// <para><c>packages</c> stays on this list now that a step may clear part of it, and asserting
    /// its survival is no weaker a claim than before. Neither prune category removes the directory:
    /// each uninstalls individual packages inside it, and a current PlatformIO's own core packages
    /// are never candidates. A run that left <c>packages</c> missing would mean something removed
    /// the folder itself, which is precisely what §5.6 exists to catch.</para>
    ///
    /// <para>Built from the directory PlatformIO reported rather than from <see cref="CoreRoot"/>.
    /// A relocated core directory would otherwise leave every one of these paths absent, and
    /// <see cref="ProtectedPath.ExistedBefore"/> would record them as never present — six assertions
    /// that pass without establishing anything, on exactly the installs where the guess about where
    /// PlatformIO lives has already been shown to be wrong.</para>
    /// </summary>
    private static IReadOnlyList<ProtectedPath> BuildProtectedPaths(PlatformIoLocations locations)
    {
        var core = locations.CoreDirectory;

        return Protect(
            (core, "PlatformIO's core directory must survive — only the cache and the unused packages within it are cleared."),
            (locations.PackagesDirectory, "Installed toolchains and frameworks; PlatformIO removes individual unused packages, never the folder."),
            (Path.Combine(core, "platforms"), "Installed development platform definitions."),
            (Path.Combine(core, "penv"), "The virtual environment PlatformIO Core itself runs in."),
            (Path.Combine(core, "python3"), "The bundled Python interpreter backing that environment."),
            (Path.Combine(core, "lib"), "Globally installed user libraries — never a cache."));
    }

    /// <summary>
    /// Ask PlatformIO where it keeps things. <c>--json-output</c> rather than scraping the human
    /// listing: the field names are part of a documented machine-readable contract, the alignment
    /// of the text table is not.
    ///
    /// <para>Which fields come back varies by version — 6.1.19 reports <c>core_dir</c> and neither
    /// of the other two — so each answer falls back to the documented default beneath it rather than
    /// to nothing.</para>
    /// </summary>
    private async Task<PlatformIoLocations> ResolveLocationsAsync(string pio, CancellationToken ct)
    {
        if (_locations is not null)
        {
            return _locations;
        }

        var outcome = await Runner
            .RunAsync(pio, "system info --json-output", ct)
            .ConfigureAwait(false);

        var reported = outcome.Succeeded ? TryReadReport(outcome.StandardOutput) : null;
        var core = ReadPath(reported, "core_dir") ?? CoreRoot;

        return _locations = new PlatformIoLocations(
            core,
            ReadPath(reported, "cache_dir") ?? Path.Combine(core, ".cache"),
            ReadPath(reported, "packages_dir") ?? Path.Combine(core, "packages"));
    }

    /// <summary>
    /// The report as a value that outlives the document holding it, which is what
    /// <see cref="JsonElement.Clone"/> is for. Three fields are read from it, and threading the
    /// document through three calls to keep it alive would put the disposal in the caller.
    /// </summary>
    private static JsonElement? TryReadReport(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            // An older PlatformIO that does not understand --json-output prints its usage text to
            // stdout and still exits zero, so malformed output here is an expected outcome rather
            // than a broken install. The documented locations are the honest fallback.
            return null;
        }
    }

    /// <summary>
    /// PlatformIO wraps each value in <c>{"value": …, "default": …}</c> in some versions and emits
    /// a bare string in others, so both shapes are read rather than assuming the current one.
    /// </summary>
    private static string? ReadPath(JsonElement? reported, string name)
    {
        if (reported is not { } root || !root.TryGetProperty(name, out var property))
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

    private static PlanNote Information(string message) => new(PlanNoteSeverity.Information, message);

    /// <summary>Where PlatformIO keeps things, as it reported them or as documented beneath it.</summary>
    private sealed record PlatformIoLocations(
        string CoreDirectory,
        string CacheDirectory,
        string PackagesDirectory);

    /// <summary>
    /// One half of the plan: the step it contributes if any, what the user is told about it, and how
    /// its measurement was obtained.
    ///
    /// The two halves are assembled rather than built by one long method, because the cache and the
    /// packages are independent subjects. A machine with an empty cache can still hold superseded
    /// toolchains, and returning early for the first would silently withhold the larger of the two.
    /// </summary>
    private sealed record PlanPart(
        CleanupStep? Step,
        IReadOnlyList<PlanNote> Notes,
        FallbackReason Fallback);
}
