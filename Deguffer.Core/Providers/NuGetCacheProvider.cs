using System.Collections.Frozen;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// NuGet's global packages folder and HTTP cache (~10 GB on the audited machine).
///
/// §5.1 in its purest form: <c>dotnet nuget locals all --clear</c> cleared four separate
/// locations, two of which were not under <c>.nuget</c> at all. A path-based cleaner would have
/// missed ~3 GB, so the plan calls the tool and lets it decide what to remove. The locations are
/// read back from <c>--list</c> purely so the reclaim can be measured and reported.
/// </summary>
public sealed class NuGetCacheProvider : CleanupProviderBase
{
    private IReadOnlyList<string>? _resolvedLocals;

    public NuGetCacheProvider(
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

    public override string Id => "nuget";

    public override string Name => "NuGet package cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The next restore re-downloads packages from your configured feeds. Projects and their configuration are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "NuGet, the package manager for .NET",
        Publisher = "Microsoft and the .NET Foundation",
        Purpose = "NuGet keeps several caches in your profile: the downloaded package archives, "
            + "the extracted global packages folder every project builds against, and the "
            + "responses from the feeds it has queried.",
        Recommendation = "Deguffer asks the .NET SDK to clear them with its own command, which "
            + "reaches locations a path-based rule would miss. A restore fetches whatever is "
            + "missing from the feeds the machine is already configured for.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["devenv", "MSBuild", "VBCSCompiler"];

    /// <summary>
    /// The two caches NuGet keeps under <c>%LOCALAPPDATA%\NuGet</c>. Its credential-provider
    /// plugins sit in the same directory and are not a cache, so anything absent here is Tier 4 by
    /// construction.
    /// </summary>
    private static readonly FrozenSet<string> LocalCacheNames =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "v3-cache", "plugins-cache");

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside. Both folders mix cache with configuration:
    /// <c>NuGet.Config</c> sits in <c>.nuget</c> beside the packages folder, and the
    /// credential-provider plugins sit under <c>%LOCALAPPDATA%\NuGet</c> beside the two HTTP caches.
    ///
    /// <para>The locations <c>dotnet nuget locals --list</c> reports are deliberately absent. They
    /// arrive from a subprocess, and a declaration Explore consults on every path has to be readable
    /// without running one. These are the documented defaults, and a declaration can only ever
    /// refuse — so naming them costs nothing and covers the ordinary machine.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        new ToolRoot(
            Path.Combine(Environment.UserProfile, ".nuget"),
            "This is NuGet's own folder. Deguffer clears the downloaded packages inside it and "
            + "nothing else, because the NuGet.Config beside them may hold credentials for your "
            + "private feeds.",
            static name => name.Equals("packages", StringComparison.OrdinalIgnoreCase)),

        new ToolRoot(
            Path.Combine(Environment.LocalAppData, "NuGet"),
            "This is NuGet's own folder. Deguffer clears the HTTP and plugin caches inside it and "
            + "nothing else, because the credential-provider plugins beside them are not a cache.",
            LocalCacheNames.Contains),

        new ToolRoot(
            Path.Combine(Environment.RoamingAppData, "NuGet", "NuGet.Config"),
            "This is your NuGet configuration, and it may hold credentials for your private feeds. "
            + "Deguffer never removes it.",
            static _ => false),
    ];

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Environment.FindExecutable("dotnet") is not null);

    /// <summary>
    /// The locals NuGet reports are configuration — <c>NUGET_PACKAGES</c>, <c>NUGET_HTTP_CACHE_PATH</c>
    /// and a <c>NuGet.Config</c> edit all move them, and a globalPackagesFolder can change between one
    /// scan and the next. Keeping the resolved list across an invalidation would measure locations
    /// NuGet has stopped using and miss the ones it now does.
    /// </summary>
    public override void InvalidateCaches()
    {
        _resolvedLocals = null;
        base.InvalidateCaches();
    }

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        var dotnet = Environment.FindExecutable("dotnet");
        if (dotnet is null)
        {
            return EmptyPlan("The .NET SDK is not installed on this machine.");
        }

        var locals = await ResolveLocalsAsync(dotnet, ct).ConfigureAwait(false);
        var present = locals.Where(LongPath.DirectoryExists).ToList();

        if (present.Count == 0)
        {
            return EmptyPlan("The .NET SDK is installed but none of its NuGet cache locations exist yet.");
        }

        var measured = await MeasureAllAsync(present, ct).ConfigureAwait(false);

        var notes = new List<PlanNote>
        {
            new(PlanNoteSeverity.Information,
                "Cleared by NuGet itself, which reaches locations outside .nuget that a folder delete would miss: " +
                string.Join(", ", present.Select(LongPath.Display))),
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
            Steps =
            [
                new RunCommandStep(dotnet, "nuget locals all --clear", "Clear all NuGet caches using NuGet's own command")
                {
                    Estimated = measured.Total,
                    MeasuredPaths = present,
                },
            ],
            ProtectedPaths = BuildProtectedPaths(),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    /// <summary>
    /// §5.2 and §5.6. NuGet.Config's location cannot be assumed — on the audited machine it lived
    /// under <c>%APPDATA%\NuGet</c> rather than beside the packages folder — so probe both.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths() => Protect(
        (Path.Combine(Environment.RoamingAppData, "NuGet", "NuGet.Config"),
            "User NuGet configuration, which may hold private feed credentials."),
        (Path.Combine(Environment.UserProfile, ".nuget", "NuGet.Config"),
            "The alternative NuGet.Config location — §5.2 says probe both."),
        (Path.Combine(Environment.UserProfile, ".nuget"),
            "The .nuget root itself must survive; only its cache contents are cleared."));

    /// <summary>
    /// Ask NuGet where its caches are. Output lines look like
    /// <c>global-packages: C:\Users\me\.nuget\packages\</c>.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveLocalsAsync(string dotnet, CancellationToken ct)
    {
        if (_resolvedLocals is not null)
        {
            return _resolvedLocals;
        }

        var outcome = await Runner.RunAsync(dotnet, "nuget locals all --list", ct).ConfigureAwait(false);

        var parsed = new List<string>();
        if (outcome.Succeeded)
        {
            foreach (var line in outcome.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf(':');
                if (separator < 0)
                {
                    continue;
                }

                var value = line[(separator + 1)..].Trim();
                if (Path.IsPathRooted(value))
                {
                    parsed.Add(value.TrimEnd('\\', '/'));
                }
            }
        }

        return _resolvedLocals = parsed.Count > 0 ? parsed : DefaultLocals();
    }

    /// <summary>Documented defaults, used only when NuGet declines to tell us.</summary>
    private IReadOnlyList<string> DefaultLocals() =>
    [
        Path.Combine(Environment.UserProfile, ".nuget", "packages"),
        Path.Combine(Environment.LocalAppData, "NuGet", "v3-cache"),
        Path.Combine(Environment.LocalAppData, "NuGet", "plugins-cache"),
        Path.Combine(Environment.TempPath, "NuGetScratch"),
    ];
}
