using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Conda's package caches — tarballs, unused unpacked packages and the channel index. Researched
/// rather than measured: no conda was installed on the machine this was written against.
///
/// <para><b>The figure is conda's own, and that is the design.</b> Everything an environment uses
/// is hard-linked out of <c>pkgs</c>, so measuring the caches directly counts the packages that
/// must stay — §5.4's over-report on a different subject. Conda's dry run already accounts for
/// its own links: its clean skips any file with more than one hard link, so what the dry run sizes
/// is exactly what the clean removes. Deguffer shows that figure (plus its own measure of the
/// index cache, which the report lists but does not size) and never derives an estimate from the
/// directories themselves. Where the dry run cannot be read, nothing is offered, because the only
/// substitute figure is the wrong one. This is <see cref="PlatformIoCacheProvider"/>'s shape with
/// the tool's accounting taken one step further.</para>
///
/// <para><b>§5.1's command, with two flags deliberately absent.</b> <c>--all</c> is not passed,
/// because it also removes conda's own log files, and a log is a record of something that already
/// happened — §3 puts those in Tier 3, and this plan is Tier 2. <c>--force-pkgs-dirs</c> is never
/// passed: it removes every writable cache whole, and conda's own help says it breaks environments
/// that link by symlink.</para>
///
/// <para><b>Tier 2, but not for the survey's reason.</b> The clean touches no environment — the
/// proposed tier's "re-creating an environment is a download" describes an operation this command
/// never performs. Tier 2 stands on two other grounds: the refill is a re-download that vendor
/// documentation puts at tens of gigabytes, which is Tier 2's own definition; and conda's unused
/// test trusts hard-link counts that its documentation warns cannot see a symlinked environment,
/// so the cautious, never-pre-selected tier is the honest one.</para>
/// </summary>
public sealed class CondaCacheProvider : CleanupProviderBase
{
    // Explicit categories rather than --all — see the class remarks for the two absences.
    // --tempfiles with no argument takes conda's own default subject (its installation prefix).
    private const string CleanCategories = "clean --index-cache --packages --tarballs --tempfiles";

    private readonly ISystemDirectories _systemDirectories;

    private string? _conda;
    private CondaInstallation? _installation;

    public CondaCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? systemDirectories = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default) =>
        _systemDirectories = systemDirectories ?? SystemDirectories.Current;

    public override string Id => "conda";

    public override string Name => "Conda package cache";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next conda install downloads packages and re-fetches the channel index. Your "
        + "environments are untouched: conda keeps every package an environment still links.";

    protected override IReadOnlyList<string> ConflictingProcessNames => ["conda", "mamba"];

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(FindConda() is not null);

    /// <summary>
    /// Both answers are configuration: <c>CONDA_EXE</c> and <c>.condarc</c> move them, and a
    /// rescan must describe the machine as it is now.
    /// </summary>
    public override void InvalidateCaches()
    {
        _conda = null;
        _installation = null;
        base.InvalidateCaches();
    }

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        var conda = FindConda();
        if (conda is null)
        {
            return EmptyPlan("Conda is not installed on this machine.");
        }

        var installation = await ResolveInstallationAsync(conda, ct).ConfigureAwait(false);
        if (installation is null)
        {
            return EmptyPlan("conda did not describe where its package caches are, so nothing is offered rather than guessed.");
        }

        var packageCaches = installation.PackageCacheDirs.Where(LongPath.DirectoryExists).ToList();
        if (packageCaches.Count == 0)
        {
            return EmptyPlan("Conda is installed but has cached nothing yet.");
        }

        var outcome = await Runner
            .RunAsync(conda, CleanCategories + " --dry-run --json", ct)
            .ConfigureAwait(false);

        // The report is the authority rather than the exit code: a conda dry run ends by raising
        // an exit exception whose code has differed between versions, so a parseable, successful
        // report is accepted from either.
        var preview = CondaReport.TryReadCleanPreview(outcome.StandardOutput);
        if (preview is null)
        {
            return EmptyPlan(
                "conda did not report what its own clean command would remove, so nothing is "
                + "offered — measuring the caches directly would count the packages your "
                + "environments still use.");
        }

        // Deguffer's own probe of the same directories, for the after-run delta. It is a far
        // larger number than the estimate, because it counts everything the environments link,
        // and that is exactly why the step carries both — see RunCommandStep.MeasuredBefore.
        var probed = await MeasureAllAsync(packageCaches, ct).ConfigureAwait(false);

        var indexCaches = packageCaches
            .Select(directory => Path.Combine(directory, "cache"))
            .Where(LongPath.DirectoryExists)
            .ToList();
        var indexMeasured = await MeasureAllAsync(indexCaches, ct).ConfigureAwait(false);

        var estimated = ScanSize.Approximate(preview.TarballBytes + preview.PackageBytes)
            + indexMeasured.Total;

        if (estimated.Reclaimable == 0)
        {
            return EmptyPlan("conda reports nothing unused in its package caches.");
        }

        var notes = new List<PlanNote>
        {
            new(PlanNoteSeverity.Information,
                $"The figure is conda's own dry run over {Describe(packageCaches)}, plus the "
                + "channel index cache. Packages your environments use are neither counted nor removed."),
            new(PlanNoteSeverity.Information,
                "conda decides a package is unused by its hard links, and its documentation warns "
                + "it cannot see an environment that links by symlink. Windows environments use "
                + "hard links or copies unless symlinks were deliberately enabled."),
        };

        if (probed.Note is { } scanNote)
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
            Steps =
            [
                new RunCommandStep(
                    conda,
                    CleanCategories + " --yes",
                    "Remove unused packages, tarballs and the channel index cache using conda's own command")
                {
                    Estimated = estimated,
                    MeasuredPaths = packageCaches,
                    MeasuredBefore = probed.Total,
                },
            ],
            ProtectedPaths = BuildProtectedPaths(installation, packageCaches),
            Notes = notes,
            Fallback = probed.Fallback,
        };
    }

    /// <summary>
    /// §5.6. What sits beside a package cache is everything conda actually installed: the
    /// installation prefix holds the base environment, <c>envs</c> holds the user's environments
    /// whose packages hard-link back into the cache being cleaned, and <c>.condarc</c> can carry
    /// private channel URLs with embedded tokens.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(
        CondaInstallation installation,
        IReadOnlyList<string> packageCaches)
    {
        var candidates = new List<(string Path, string Reason)>();

        if (installation.RootPrefix is { } root)
        {
            candidates.Add((root, "The conda installation itself, including its base environment."));
        }

        candidates.AddRange(installation.EnvironmentDirs
            .Select(directory => (directory, "Your environments — every package they use stays in place.")));

        candidates.Add((
            Path.Combine(Environment.UserProfile, ".condarc"),
            "Your conda configuration, which may name private channels and their tokens."));

        candidates.AddRange(packageCaches
            .Select(directory => (directory, "The package cache directory itself must survive — only unused contents are removed.")));

        return Protect([.. candidates]);
    }

    /// <summary>
    /// Find conda without assuming it is on <c>PATH</c>, because its installer does not put it
    /// there by default. In order: <c>PATH</c>; <c>CONDA_EXE</c>, which conda's own shell
    /// integration sets and a user can set globally; then the vendors' documented default install
    /// locations for the current user and for all users.
    /// </summary>
    private string? FindConda()
    {
        if (_conda is not null)
        {
            return _conda;
        }

        var configured = LongPath.Configured(Environment.GetEnvironmentVariable("CONDA_EXE"));

        return _conda = Environment.FindExecutable("conda")
            ?? (configured is not null && LongPath.FileExists(configured) ? configured : null)
            ?? DefaultInstallations().FirstOrDefault(LongPath.FileExists);
    }

    private IEnumerable<string> DefaultInstallations()
    {
        string[] products = ["anaconda3", "miniconda3", "miniforge3"];
        string[] roots = [Environment.UserProfile, Environment.LocalAppData, _systemDirectories.ProgramData];

        return roots.SelectMany(
            _ => products,
            (root, product) => Path.Combine(root, product, "Scripts", "conda.exe"));
    }

    private async Task<CondaInstallation?> ResolveInstallationAsync(string conda, CancellationToken ct)
    {
        if (_installation is not null)
        {
            return _installation;
        }

        var outcome = await Runner.RunAsync(conda, "info --json", ct).ConfigureAwait(false);

        return _installation = outcome.Succeeded
            ? CondaReport.TryReadInstallation(outcome.StandardOutput)
            : null;
    }

    private static string Describe(IReadOnlyList<string> paths) =>
        string.Join(", ", paths.Select(LongPath.Display));
}
