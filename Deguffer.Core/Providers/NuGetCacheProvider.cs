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
public sealed class NuGetCacheProvider : CleanupProviderBase, ITemporaryFolderTenant
{
    private IReadOnlyList<string>? _resolvedLocals;

    public NuGetCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default,
            volumes: volumes)
    {
    }

    public override string Id => "nuget";

    public override string Name => "NuGet package cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

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
    /// The two caches NuGet keeps under <c>%LOCALAPPDATA%\NuGet</c>: the HTTP cache, and the cache
    /// of what each plugin said it can do. Anything else there is not a cache NuGet's command
    /// clears, so it is Tier 4 by construction.
    /// </summary>
    private static readonly FrozenSet<string> LocalCacheNames =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "v3-cache", "plugins-cache");

    /// <summary>The one cache NuGet keeps under <c>.nuget</c>.</summary>
    private static readonly FrozenSet<string> ProfileCacheNames =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "packages");

    private const string NotACacheReason =
        "Not one of NuGet's caches, so NuGet's own command leaves it alone.";

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside. <c>.nuget</c> mixes cache with configuration:
    /// <c>NuGet.Config</c> and the credential-provider plugins under <c>plugins</c> sit beside the
    /// packages folder. <c>%LOCALAPPDATA%\NuGet</c> holds two caches, and nothing else in it is one.
    ///
    /// <para>The locations <c>dotnet nuget locals --list</c> reports are deliberately absent, here
    /// and from <see cref="DiscoverToolRootsAsync"/>. Each is a cache the plan clears, and the plan
    /// protects nothing it derives from them, so a declaration over one could only refuse what the
    /// Storage page offers. These are the documented defaults, and a declaration can only ever
    /// refuse — so naming them costs nothing and covers the ordinary machine.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        ToolRoot.Folders(
            Path.Combine(Environment.UserProfile, ".nuget"),
            "This is NuGet's own folder. Deguffer clears the downloaded packages inside it and "
            + "nothing else, because the NuGet.Config beside them may hold credentials for your "
            + "private feeds, and the plugins beside them sign in to those feeds.",
            ProfileCacheNames.Contains),

        ToolRoot.Folders(
            Path.Combine(Environment.LocalAppData, "NuGet"),
            "This is NuGet's own folder. Deguffer clears the HTTP and plugin caches inside it and "
            + "nothing else, because nothing else in it is one of NuGet's caches.",
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

    /// <summary>
    /// NuGet's scratch folder, <c>NuGetScratch</c>, which its own command clears and which sits in
    /// the temporary folder.
    ///
    /// <para>Matched on the folder holding each local rather than on the name, because NuGet says
    /// where its locals are and the answer is configuration. The comparison is between folders: NuGet
    /// reports the temporary folder the way its own process sees it, which on a profile with a long
    /// folder name is the 8.3 short form, and <c>NUGET_SCRATCH</c> may name it through a letter
    /// <c>subst</c> made, or another mount of its volume.</para>
    /// </summary>
    public async Task<IReadOnlyList<string>> ClaimedEntriesAsync(
        IReadOnlyList<string> folders,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folders);

        if (Environment.FindExecutable("dotnet") is not { } dotnet)
        {
            return [];
        }

        var locals = await ResolveLocalsAsync(dotnet, ct).ConfigureAwait(false);
        var holders = locals
            .Select(local => (Local: local, Parent: Path.GetDirectoryName(local)))
            .Where(local => local.Parent is not null)
            .Select(local => (local.Local, Holder: Reach(local.Parent!)))
            .ToList();

        return
        [
            .. from folder in folders
               let reached = Reach(Path.TrimEndingDirectorySeparator(folder))
               from local in holders
               where reached.IsSameAs(local.Holder)
               let entry = Path.Combine(folder, Path.GetFileName(local.Local))

               // NuGet names its scratch folder whether or not it has made it, and a claim on an
               // entry that is not there would have the other row say it left something out.
               where LongPath.ProbeDirectory(entry) is not PathPresence.Absent
               select entry,
        ];
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var dotnet = Environment.FindExecutable("dotnet");
        if (dotnet is null)
        {
            return EmptyPlan("The .NET SDK is not installed on this machine.");
        }

        var locals = await ResolveLocalsAsync(dotnet, ct).ConfigureAwait(false);
        var found = ReachedDirectories.Of(locals);

        if (NothingToPlanFor(
                found,
                "The .NET SDK is installed but none of its NuGet cache locations exist yet.") is { } nothing)
        {
            return nothing;
        }

        var present = found.Present;
        var measured = await MeasureAllAsync(present, ct).ConfigureAwait(false);

        var notes = new List<PlanNote>
        {
            new(PlanNoteSeverity.Information,
                "Cleared by NuGet itself, which reaches locations outside .nuget that a folder delete would miss: " +
                string.Join(", ", present.Select(LongPath.Display))),
        };

        // NuGet's command clears these too, so they are named rather than dropped: the figure above
        // leaves them out, by an amount nobody could measure.
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
            Steps =
            [
                new RunCommandStep(dotnet, "nuget locals all --clear", "Clear all NuGet caches using NuGet's own command")
                {
                    Estimated = measured.Total,
                    MeasuredPaths = present,
                },
            ],
            ProtectedPaths = BuildProtectedPaths(locals),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = found.CouldNotBeReached,
        };
    }

    /// <summary>
    /// §5.2 and §5.6. NuGet.Config's location cannot be assumed — on the audited machine it lived
    /// under <c>%APPDATA%\NuGet</c> rather than beside the packages folder — so probe both.
    ///
    /// <para>Both folders NuGet's command clears inside are named, and so is every child of them
    /// that is not one of its caches. Each is a sibling of a cache the command empties, which is
    /// exactly where an over-broad rule takes one with the other.</para>
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(IReadOnlyList<string> locals)
    {
        var profile = Path.Combine(Environment.UserProfile, ".nuget");
        var local = Path.Combine(Environment.LocalAppData, "NuGet");

        return Protect(
        [
            (Path.Combine(Environment.RoamingAppData, "NuGet", "NuGet.Config"),
                "User NuGet configuration, which may hold private feed credentials."),
            (Path.Combine(profile, "NuGet.Config"),
                "The alternative NuGet.Config location — §5.2 says probe both."),
            (profile,
                "The .nuget root itself must survive; only its cache contents are cleared."),
            (Path.Combine(profile, "plugins"),
                "The credential-provider plugins NuGet runs to sign in to private feeds."),
            (local,
                "NuGet's folder in your local profile must survive; only the caches inside it are cleared."),
            .. Spared(profile, ProfileCacheNames, locals),
            .. Spared(local, LocalCacheNames, locals),
        ]);
    }

    /// <summary>
    /// The children of <paramref name="root"/> that NuGet's command leaves standing: every one not
    /// named in <paramref name="caches"/>, links included.
    ///
    /// <para>A child holding a location NuGet reported is left out. NuGet's settings can move a
    /// cache into any folder, and the command then empties it, so asserting that the folder holding
    /// it is unchanged would fail a successful run. Both sides are compared as folders, because NuGet
    /// reports a location the way its own process sees it, which can be the 8.3 short form, and a
    /// setting may name it through a letter <c>subst</c> made, or another mount of its volume.</para>
    /// </summary>
    private IEnumerable<(string Path, string Reason)> Spared(
        string root,
        FrozenSet<string> caches,
        IReadOnlyList<string> locals)
    {
        var scan = ChildDirectories.Under(root);
        var reported = locals.Select(Reach).ToList();

        return scan.Directories
            .Select(child => (child.Name, Path: LongPath.Display(child.FullName), Reason: NotACacheReason))
            .Concat(scan.Links.Select(link => (link.Name, Path: LongPath.Display(link.FullName), Reason: CacheLevelWalk.LinkReason)))
            .Where(child => !caches.Contains(child.Name)
                && !reported.Exists(Reach(child.Path).Holds))
            .Select(child => (child.Path, child.Reason));
    }

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
