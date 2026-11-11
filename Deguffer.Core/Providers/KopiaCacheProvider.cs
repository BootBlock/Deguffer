using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The local caches Kopia, the backup program, keeps for each repository it is connected to. Researched
/// rather than measured: no Kopia was installed on the machine this was written against.
///
/// <para><b>Tier 1.</b> Kopia: "Cache can be cleared on demand by <c>kopia cache clear</c> or by simply
/// removing appropriate files. It is always safe to remove files from cache." Every byte is a copy of
/// something in the repository, and the cost of removing it is a slower next run.</para>
///
/// <para><b>§5.1 has no route Deguffer can take, so the folders go by path.</b> Kopia's
/// <c>cache clear</c> and <c>cache info --path</c> both open the repository before they act: they
/// need its password and a connection to wherever the backups are stored, which may be a cloud
/// service, and a scan has no business making either. What <c>cache info --path</c> prints is what the
/// connection file records, so Deguffer reads the file instead (<see cref="KopiaCacheLayout"/>).
/// <c>cache clear</c> itself only removes the cache folder and makes it again.</para>
///
/// <para><b>§5.2, and the trap in it.</b> Kopia's configuration is in <c>%APPDATA%\kopia</c> and its
/// caches in <c>%LOCALAPPDATA%\kopia</c>. Taking one for the other would remove the user's connection to
/// their backups. So the configuration folder and every connection file in it are asserted to survive,
/// and are never walked for anything to remove. In each cache folder only the folders Kopia caches into
/// are recognised. The format records Kopia caches as files, its logs beside the caches, and anything
/// else are Tier 4.</para>
///
/// <para><b>Nothing while Kopia runs.</b> Kopia's own <c>cache clear</c> closes the repository before it
/// removes the cache, and KopiaUI keeps a Kopia server running for as long as it is open, so while
/// either is in the process table the caches are held back, and the clean asks again before it removes
/// anything.</para>
/// </summary>
public sealed class KopiaCacheProvider : CleanupProviderBase
{
    private static readonly IReadOnlyList<string> ProcessNames = ["kopia", "KopiaUI"];

    private const string HeldReason =
        "Kopia is running, and Kopia closes a repository before it clears that repository's cache, so the cache "
        + "is left alone. Close KopiaUI and any Kopia command, then scan again.";

    private const string ConfigReason =
        "This is Kopia's configuration folder. It holds your connections to your repositories and the credentials "
        + "to reach them, so Deguffer removes nothing in it.";

    private const string LocalReason =
        "This is Kopia's folder for its caches and its logs. Deguffer removes only what Kopia cached inside each "
        + "cache folder.";

    private const string CacheReason =
        "This is Kopia's cache for one repository connection. Only the folders Kopia caches into may be removed.";

    private readonly ISystemDirectories _system;
    private readonly RowDeclarations _declarations;
    private readonly PassMemo<KopiaCacheLayout> _layout = new();

    /// <param name="declarations">
    /// What every row in the planner offers, so something another row removes from a folder this row
    /// walks is not asserted here. See <see cref="RowDeclarations"/>.
    /// </param>
    public KopiaCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        RowDeclarations? declarations = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
        _declarations = declarations ?? new RowDeclarations();
    }

    public override string Id => "kopia-cache";

    public override string Name => "Kopia local cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override string WhatHappensOnNextUse =>
        "Your next Kopia run re-downloads this metadata from the repository. Nothing in your backups changes.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "Kopia and KopiaUI, a backup program",
        Publisher = "the Kopia project",
        Purpose = "Kopia keeps a local copy of each repository's indexes, folder listings and recently read file "
            + "contents, so that a backup or a restore does not read them again from the repository, which may "
            + "be on a slow disk or in the cloud.",
        Recommendation = "Kopia's documentation says it is always safe to remove files from its cache. Deguffer "
            + "removes the folders Kopia caches into, never Kopia's connections to your repositories, and nothing "
            + "while Kopia is running.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ProcessNames;

    /// <summary>
    /// §5.2 for Explore: the configuration folder refuses everything in it, and Kopia's local folder
    /// recognises only its cache folders, never its logs.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        new ToolRoot(KopiaCacheLayout.ConfigFolder(Environment), ConfigReason, static _ => false),
        ToolRoot.Folders(KopiaCacheLayout.LocalFolder(Environment), LocalReason, KopiaCacheLayout.IsCacheName),
    ];

    /// <summary>
    /// Each cache folder recognises only the folders Kopia caches into, and nothing at all while Kopia
    /// runs. Each connection file, and the password file Kopia may keep beside it, is refused wherever
    /// <see cref="KopiaCacheLayout.ConfigVariable"/> put it.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var running = KopiaIsRunning();
        var layout = Layout;

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. layout.Caches.Select(cache => running
                ? new ToolRoot(cache, HeldReason, static _ => false)
                : ToolRoot.Of(cache, CacheReason, KopiaCacheLayout.Children)),
            // Only a file outside the configuration folder, which ToolRoots already refuses whole. Declared
            // roots at one folder are pooled, so one sparing a single name there would open the rest.
            .. layout.ConnectionFiles
                .Select(file => Path.GetDirectoryName(file)!)
                .Where(folder => !folder.Equals(KopiaCacheLayout.ConfigFolder(Environment), StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(folder => ToolRoot.Sparing(
                    folder,
                    ConfigReason,
                    layout.ConnectionFiles
                        .Where(file => Path.GetDirectoryName(file)!.Equals(folder, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(file => new[] { file, KopiaCacheLayout.PasswordFileOf(file) })
                        .Select(file => Path.GetFileName(file)))),
        ]);
    }

    /// <summary>The recognised folders of every cache folder found, which is all the clean ever removes.</summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            .. Layout.Caches.SelectMany(cache => KopiaCacheLayout.Children.DisposableNames
                .Select(child => CleanedPlace.Whole(Path.Combine(cache, child)))),
        ]);

    /// <summary>
    /// A recognised folder in a cache folder, never Kopia on the PATH: a cache outlives the Kopia that
    /// wrote it, and nothing here runs Kopia.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Layout.Unreadable.Count > 0 || Layout.Caches.Any(cache => KopiaCacheLayout.Children.DisposableNames
            .Any(child => LongPath.DirectoryMayExist(Path.Combine(cache, child)))));

    /// <summary>The connection files, the variables and the disk can all change between one scan and the next.</summary>
    public override void InvalidateCaches()
    {
        _layout.Forget();
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var layout = Layout;
        var notes = new List<PlanNote>(layout.Notes);

        if (layout.Caches.Count == 0 && layout.Unreadable.Count > 0)
        {
            return EmptyPlan(UnreadableRoot.WhyItCouldNotBeReached(layout.Unreadable[0])) with
            {
                Notes = notes,
                HasUnreadableRoot = true,
            };
        }

        if (layout.Caches.Count == 0)
        {
            return notes.Count > 0
                ? UnexaminedPlan(string.Join(" ", notes.Select(note => note.Message)))
                : EmptyPlan("Kopia has cached nothing for this account.");
        }

        var running = KopiaIsRunning();
        var stillClosed = new RunningProcessCheck(Inspector, ProcessNames);
        var targets = new List<DeletionTarget>();
        var survivors = new List<(string Path, string Reason)>();
        var held = new List<(string Path, string Reason)>();
        var unreadable = layout.Unreadable.Count > 0;
        var declined = false;

        foreach (var cache in layout.Caches)
        {
            survivors.Add((cache, "Kopia's cache folder must survive. Only the folders Kopia caches into are removed."));

            if (running)
            {
                held.Add((cache, HeldReason));
                continue;
            }

            var walk = CacheLevelWalk.Under(KopiaCacheLayout.Levels, cache, ct);
            var heading = LongPath.Display(cache);

            targets.AddRange(walk.Targets.Select(target => target with { Group = heading, UseCheck = stillClosed }));
            survivors.AddRange(walk.Declined);
            survivors.AddRange(await walk.SurvivorsAsync(_declarations, ct).ConfigureAwait(false));
            notes.AddRange(walk.Notes);
            unreadable |= walk.Unreadable;
            declined |= walk.Declined.Count > 0;
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (held.Count > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left Kopia's cache alone: {HeldReason}"));
        }

        if (steps.Count > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "Deguffer removes what Kopia cached by path. Kopia's own 'kopia cache clear' opens the repository "
                + "first, which needs its password and a connection to where your backups are stored."));
        }

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            // Held first, so a cache held back is named for why rather than as the folder only some of the
            // contents of are removed. The connections next, because losing one is the failure this row
            // exists to rule out.
            ProtectedPaths = await ProtectAsync(
                [
                    .. held,
                    (KopiaCacheLayout.ConfigFolder(Environment), "Kopia's configuration folder, with your connections to your repositories."),
                    .. layout.ConnectionFiles.SelectMany(file => new[]
                    {
                        (file, "A Kopia connection: where a repository is and how to reach it."),
                        (KopiaCacheLayout.PasswordFileOf(file), "The password Kopia keeps for this connection where Windows does not keep it."),
                    }),
                    (KopiaCacheLayout.LocalFolder(Environment), "Kopia's folder for its caches and its logs."),
                    .. survivors,
                ],
                ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = measured.Fallback,
            WasNotExamined = steps.Count == 0 && (held.Count > 0 || declined || unreadable || layout.Notes.Count > 0),
            HasUnreadableRoot = unreadable,
        };
    }

    private KopiaCacheLayout Layout => _layout.Get(() => KopiaCacheLayout.Find(Environment, _system, Volumes));

    private bool KopiaIsRunning() => Inspector.FindRunning(ProcessNames).Count > 0;
}
