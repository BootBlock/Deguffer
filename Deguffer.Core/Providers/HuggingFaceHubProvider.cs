using System.Collections.Frozen;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Hugging Face hub cache: every model, dataset and Space a Python library downloaded through
/// <c>huggingface_hub</c> (Transformers, Diffusers, faster-whisper and many more), one item per
/// repository. Measured at 3.4 GB in two repositories on one workstation, and tens of gigabytes on a
/// machine that runs local models.
///
/// <para><b>Tier 3, never pre-selected.</b> <c>docs/todo/unreached-locations.md</c> §9: a model that is
/// gated, private, withdrawn or replaced under the same name cannot be downloaded again, so the cache
/// is the only copy the user may have.</para>
///
/// <para><b>§5.1, and the tool answers for itself.</b> <c>hf env</c> says which version is installed
/// and where the hub cache is, <c>hf cache ls --format json</c> lists the repositories with their
/// sizes, and <c>hf cache rm &lt;id&gt; --format json --yes</c> removes one, every revision of it. Each
/// is told the folder the plan examined with <c>--cache-dir</c>. In JSON form a confirmation raises
/// rather than prompts, so the command cannot wait on a question.</para>
///
/// <para><b>Never by path, and only from 1.32.</b> From 1.32 the cache can keep a shared blob store in
/// <c>hub\blobs</c>, marked by <c>.huggingface-shared-blobs</c>, and a repository's files are then links
/// into it. Deleting a repository folder leaves its shared files behind, and deleting <c>hub\blobs</c>
/// breaks every repository that links into it. Older versions of the tool leave a removed repository's
/// shared files in the store, and report <c>hub\blobs</c> as a repository they cannot read, so below
/// 1.32 the row offers nothing and says why.</para>
///
/// <para><b>Not <c>hf cache prune</c>.</b> It removes every revision no ref names, and a revision a
/// program pinned by its full commit hash is written with no ref
/// (<c>_cache_commit_hash_for_specific_revision</c>), so prune cannot tell a superseded revision from a
/// pinned one.</para>
///
/// <para><b>§5.2.</b> Only the <c>hf</c> command touches anything inside the hub cache, and only
/// repositories it lists, in the folder its own naming rule gives them. Everything else in the cache,
/// <c>CACHEDIR.TAG</c>, <c>version.txt</c>, <c>.locks</c>, the shared <c>blobs</c> and any folder the tool
/// did not list, is Tier 4. So is every sibling of <c>hub</c> in <c>HF_HOME</c>, the access tokens in
/// <c>token</c> and <c>stored_tokens</c> first among them.</para>
///
/// <para><b>In use.</b> The tool keeps no lock while a model is loaded, so each repository is asked
/// about by its files. See <see cref="HuggingFaceRepositoryUse"/>.</para>
///
/// <para><b>No check that the command still means the plan's repository.</b> A repository's id names
/// one repository for as long as it exists, and <c>--cache-dir</c> fixes the cache the command looks
/// in, so what the arguments reach cannot move between the preview and the clean.</para>
/// </summary>
public sealed class HuggingFaceHubProvider : CleanupProviderBase
{
    /// <summary>
    /// What sits at the top of the hub cache beside the repositories, with why each survives:
    /// <c>huggingface_hub</c>'s own tag, locks and shared store, and the <c>version.txt</c> Transformers
    /// writes there to record which layout it last moved the cache to.
    /// </summary>
    public static readonly FrozenDictionary<string, string> CacheFiles = new Dictionary<string, string>
    {
        ["CACHEDIR.TAG"] = "The tag that tells backup tools this folder is a cache.",
        ["version.txt"] = "The record Transformers keeps of which layout it last moved the cache to.",
        [".locks"] = "The locks huggingface_hub takes while it downloads.",
        ["blobs"] = "The shared store of files several repositories link into. Only 'hf cache rm' may remove a file from it.",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tool's own programs, which write into the cache while they run. The library itself runs
    /// inside Python and is asked about by its files instead.
    /// </summary>
    private static readonly IReadOnlyList<string> HfProcessNames = ["hf", "huggingface-cli"];

    private const string HubRootReason =
        "This is the Hugging Face hub cache. Deguffer removes a repository from it only with 'hf cache rm', "
        + "because a repository's files can be links into a store other repositories share.";

    private const string HomeRootReason =
        "This is the Hugging Face folder. It holds your access tokens beside the hub cache, so Deguffer "
        + "removes nothing in it directly.";

    private readonly ISystemDirectories _system;
    private readonly ILiveTreeInspector _liveTrees;
    private readonly PassMemo<Task<HuggingFaceEnvironment?>> _environment = new();

    public HuggingFaceHubProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IVolumeInventory? volumes = null,
        ILiveTreeInspector? liveTrees = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
        _liveTrees = liveTrees ?? LiveTreeInspector.Default;
    }

    public override string Id => "huggingface-hub";

    public override string Name => "Hugging Face models and datasets";

    public override SafetyTier Tier => SafetyTier.UserData;

    /// <summary>Each repository is a model or dataset the user may not be able to download again.</summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "A program that needs a removed model or dataset downloads it again, if the Hugging Face Hub still "
        + "offers it. One that was gated, private, withdrawn or replaced under the same name may not come "
        + "back. Your access tokens and every repository you keep are untouched.";

    /// <summary>
    /// The layout, the shared store included, and every command's output were seen on a cache that
    /// <c>huggingface_hub</c> 1.31, 1.32 and 2.4 had filled.
    /// </summary>
    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "the Hugging Face Hub's Python library, which Transformers, Diffusers and many other "
            + "machine-learning tools download through",
        Publisher = "Hugging Face",
        Purpose = "Every model, dataset and Space a program downloads from the Hugging Face Hub is kept in "
            + "one cache, every revision of it, so the next program that asks for it does not download it "
            + "again. A model is often several gigabytes.",
        Recommendation = "Deguffer lists each repository and removes only the ones you choose, with Hugging "
            + "Face's own 'hf cache rm', which knows which files other repositories share. Nothing is "
            + "chosen for you, because a model may no longer be offered for download.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => HfProcessNames;

    /// <summary>Where the hub cache is when no variable moves it.</summary>
    public string DefaultHubCache => HuggingFaceLocations.DefaultHub(Environment);

    /// <summary>
    /// The hub cache and the folder above it, as both readings of the variables name them, recognising
    /// nothing in either: a repository goes only through <c>hf cache rm</c>, and the folder above it
    /// holds the access tokens. The folders <c>hf</c> reports are declared in
    /// <see cref="DiscoverToolRootsAsync"/>, because only a subprocess can say.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots
    {
        get
        {
            var readings = new[] { Documented(VariableReading.Tool), Documented(VariableReading.Current) };

            return
            [
                .. readings.Select(folders => folders.Home).OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(home => new ToolRoot(home, HomeRootReason, static _ => false)),
                .. readings.Select(folders => folders.Hub).OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(hub => new ToolRoot(hub, HubRootReason, static _ => false)),
            ];
        }
    }

    /// <summary>The hub cache <c>hf</c> reports, and the Hugging Face folder holding its access tokens.</summary>
    public override async Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        if (Environment.FindExecutable("hf") is not { } hf
            || await ResolveEnvironmentAsync(hf, ct).ConfigureAwait(false) is not { HubCache: { } hub } reported
            || WhyNotExamined(hub) is not null)
        {
            return [];
        }

        return
        [
            new ToolRoot(hub, HubRootReason, static _ => false),
            .. reported.Credentials
                .Select(Path.GetDirectoryName)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(home => WhyNotExamined(home) is null)
                .Select(home => new ToolRoot(home, HomeRootReason, static _ => false)),
        ];
    }

    /// <summary>
    /// Every hub cache <c>hf cache rm</c> can be sent to: the one <c>hf</c> reports and the documented
    /// ones, because a variable can change after a duplicate search has asked. Whole, because the
    /// command removes from the shared store as well as the repository's folder.
    /// </summary>
    public override async Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default)
    {
        List<string> caches = [DefaultHubCache];

        if (Documented(VariableReading.Tool).Hub is { } documented)
        {
            caches.Add(documented);
        }

        if (Environment.FindExecutable("hf") is { } hf
            && await ResolveEnvironmentAsync(hf, ct).ConfigureAwait(false) is { HubCache: { } hub }
            && WhyNotExamined(hub) is null)
        {
            caches.Add(hub);
        }

        return [.. caches.Distinct(StringComparer.OrdinalIgnoreCase).Select(CleanedPlace.Whole)];
    }

    /// <summary>
    /// <c>hf</c> on the PATH and the hub cache it reports existing. A cache Windows would not describe,
    /// and an <c>hf</c> that would not say where its cache is or is too old to remove from it, count as
    /// present, because only the plan can say why nothing is offered.
    /// </summary>
    public override async Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Environment.FindExecutable("hf") is { } hf
        && (await ResolveEnvironmentAsync(hf, ct).ConfigureAwait(false) is not { HubCache: { } hub }
            || LongPath.ProbeDirectory(hub) is not PathPresence.Absent);

    /// <summary>
    /// Every answer here is the tool's, which an update or a variable can change between one scan and
    /// the next, and the process table changes all the time.
    /// </summary>
    public override void InvalidateCaches()
    {
        _environment.Forget();
        _liveTrees.Invalidate();
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (Environment.FindExecutable("hf") is not { } hf)
        {
            return EmptyPlan("The Hugging Face command-line tool, hf, is not installed on this machine.");
        }

        if (await ResolveEnvironmentAsync(hf, ct).ConfigureAwait(false) is not { } reported)
        {
            return UnexaminedPlan(
                "'hf env' did not answer, so Deguffer cannot tell which version of the Hugging Face tool is "
                + "installed or where its cache is. Nothing is offered.");
        }

        if (!reported.KnowsSharedBlobs)
        {
            return UnexaminedPlan(
                (reported.Version is { } version ? $"The Hugging Face tool installed is version {version}. " : "The Hugging Face tool did not say which version it is. ")
                + $"Deguffer needs {HuggingFaceEnvironment.SharedBlobsVersion} or later, because older versions "
                + "do not remove a repository's files from the store that repositories share. Update it with "
                + "'pip install -U huggingface_hub', then scan again.");
        }

        if (reported.HubCache is not { } hub)
        {
            return UnexaminedPlan("'hf env' did not say where the Hugging Face hub cache is, so nothing is offered.");
        }

        if (WhyNotExamined(hub) is { } declined)
        {
            return EmptyPlan(
                $"The Hugging Face hub cache is set to '{hub}', and Deguffer will not run 'hf cache rm' there: "
                + $"{declined} It is leaving it alone.");
        }

        if (NothingToPlanFor(hub, $"The Hugging Face tool has downloaded nothing yet ({hub} does not exist).", out var hubIsLink) is { } nothing)
        {
            return nothing;
        }

        if (hubIsLink)
        {
            return UnexaminedPlan(
                $"Leaving '{hub}' alone: it is a link to somewhere else, and Deguffer does not look through a link.");
        }

        // §5.6 asserts every entry beside the repositories offered, read from this listing, so a cache
        // that will not be listed offers nothing.
        if (FolderEntries.Of(hub) is not { } entries)
        {
            return UnreadableRootPlan(hub);
        }

        var listed = await Runner.RunAsync(hf, HuggingFaceCommands.List(hub), ToolRun.Query, ct).ConfigureAwait(false);

        if (!listed.Succeeded || HuggingFaceCacheListing.TryRead(listed.StandardOutput) is not { } repositories)
        {
            return UnexaminedPlan(
                "The Hugging Face tool did not list its cache in a form Deguffer can read, so nothing is offered.");
        }

        var notes = new List<PlanNote>
        {
            Information($"Hugging Face's tool, version {reported.Version}, keeps its hub cache in {hub}."),
        };

        var candidates = Locate(hub, repositories, notes);
        var use = HuggingFaceRepositoryUse.Partition(
            _liveTrees, candidates.ToDictionary(item => item.Folder, item => item.Repository.Id, StringComparer.OrdinalIgnoreCase), ct);
        var unused = candidates.Where(item => use.Unused.Contains(item.Folder)).ToList();
        notes.AddRange(use.Notes);

        var steps = unused.Select(item => (CleanupStep)new RunCommandStep(
            hf,
            HuggingFaceCommands.Remove(item.Repository.Id, hub),
            "Remove this repository using Hugging Face's own command")
        {
            // A removal ended part-way leaves a revision whose links point at files that are gone, and
            // the shared store's record of who links to what half written. Left to finish.
            WhenCancelled = CancelledCommand.Finishes,
            Estimated = ScanSize.Approximate(item.Repository.Bytes),
            MeasuredPaths = [item.Folder],
            MeasuredBy = new HuggingFaceRepositorySize(Runner, hf, hub, item.Repository.Id),
            Removes = item.Folder,
            TargetCheck = new HuggingFaceRepositoryUse(_liveTrees, item.Folder),
            Identity = new ItemIdentity(item.Repository.Id, item.Repository.RepoId),
            Group = GroupOf(item.Repository.Type),
        }).ToList();

        if (steps.Count > 0)
        {
            notes.Add(Information(
                "Each size is the Hugging Face tool's own figure. A file two repositories share is counted in "
                + "both, and removing one of them leaves it for the other."));
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
            ProtectedPaths = BuildProtectedPaths(hub, reported, entries, unused.Select(item => item.Folder), use.Held),
            Notes = notes,
        };
    }

    /// <summary>
    /// Each repository listed, with its folder, where the folder is where the tool's naming rule puts it
    /// and is a folder of its own. The command is told an id and the figure and the §5.6 negative are
    /// about a folder, so the two have to be the same repository.
    /// </summary>
    private static List<(HuggingFaceRepository Repository, string Folder)> Locate(
        string hub,
        IReadOnlyList<HuggingFaceRepository> repositories,
        List<PlanNote> notes)
    {
        var located = new List<(HuggingFaceRepository, string)>(repositories.Count);

        foreach (var repository in repositories)
        {
            var folder = LongPath.Display(Path.Combine(hub, repository.Folder));

            switch (LongPath.ProbeDirectory(folder, out var isLink))
            {
                case PathPresence.Absent:
                    notes.Add(Information(
                        $"Leaving {repository.Id} alone: the Hugging Face tool lists it, but it is not in the folder the tool keeps it in."));
                    continue;

                case PathPresence.Refused:
                    notes.Add(UnreadableRoot.UnreachedNote(folder));
                    continue;
            }

            if (isLink is not false)
            {
                notes.Add(Information(
                    $"Leaving {repository.Id} alone: its folder is a link to somewhere else, and Deguffer does not look through a link."));
                continue;
            }

            located.Add((repository, folder));
        }

        return located;
    }

    /// <summary>
    /// §5.6. Everything in the hub cache beside the repositories offered is the point: the tool's own
    /// files, the shared store every repository may link into, each repository held back, and anything
    /// the tool did not list, which has the same shape as the folders removed beside it. Then the
    /// Hugging Face folder above, where the access tokens are.
    ///
    /// <para>The shared store is asserted standing and nothing more. <c>hf cache rm</c> removes the
    /// files in it that only the removed repository used, and keeps the store.</para>
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(
        string hub,
        HuggingFaceEnvironment reported,
        IReadOnlyList<FileSystemInfo> entries,
        IEnumerable<string> offered,
        IReadOnlyList<(string Path, string Reason)> held)
    {
        var excluded = new HashSet<string>(offered, StringComparer.OrdinalIgnoreCase);
        var heldReasons = held.ToDictionary(item => item.Path, item => item.Reason, StringComparer.OrdinalIgnoreCase);

        var inCache = entries
            .Select(entry => LongPath.Display(entry.FullName))
            .Where(path => !excluded.Contains(path))
            .Select(path => (path, CacheFiles.GetValueOrDefault(Path.GetFileName(path))
                ?? heldReasons.GetValueOrDefault(path)
                ?? "Not a repository Deguffer offers, so it must survive."));

        // The Hugging Face folder is the one the tool keeps its tokens in. Its other children are
        // asserted only where it holds the hub cache, because a hub cache moved on its own sits beside
        // folders that are nothing of the tool's.
        var home = reported.Credentials.Select(Path.GetDirectoryName).OfType<string>().FirstOrDefault();
        var parent = Path.GetDirectoryName(hub);
        var besideHub = home is not null && parent is not null && home.Equals(parent, StringComparison.OrdinalIgnoreCase)
            ? FolderEntries.Of(parent)?
                .Select(entry => LongPath.Display(entry.FullName))
                .Where(path => !path.Equals(hub, StringComparison.OrdinalIgnoreCase))
                .Select(path => (path, "Beside the hub cache in the Hugging Face folder, and nothing removes it."))
                ?? []
            : [];

        return Protect(
        [
            (hub, "The Hugging Face hub cache must survive: only the repositories chosen are removed from it."),
            .. reported.Credentials.Select(file => (file, "Your Hugging Face access tokens.")),
            .. reported.Assets is { } assets ? [(assets, "Files libraries downloaded for themselves beside the hub cache.")] : Array.Empty<(string, string)>(),
            .. inCache,
            .. besideHub,
        ]);
    }

    /// <summary>The heading a repository is listed under, by what kind of repository it is.</summary>
    private static string GroupOf(HuggingFaceRepositoryType type) => type switch
    {
        HuggingFaceRepositoryType.Model => "Models",
        HuggingFaceRepositoryType.Dataset => "Datasets",
        HuggingFaceRepositoryType.Space => "Spaces",
        HuggingFaceRepositoryType.Kernel => "Kernels",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>
    /// Why <paramref name="folder"/> must not be examined as Hugging Face's, as the end of a sentence,
    /// or null where it may be.
    /// </summary>
    private string? WhyNotExamined(string folder) =>
        ConfiguredFolder.WhyNotOwned(
            folder, Environment, _system, Volumes, TempRoots.Resolve(Environment, _system, Volumes).AccountFolders);

    private Task<HuggingFaceEnvironment?> ResolveEnvironmentAsync(string hf, CancellationToken ct) =>
        _environment.Get(() => AskEnvironmentAsync(hf, ct));

    private async Task<HuggingFaceEnvironment?> AskEnvironmentAsync(string hf, CancellationToken ct)
    {
        var outcome = await Runner.RunAsync(hf, HuggingFaceCommands.Environment, ToolRun.Query, ct).ConfigureAwait(false);

        return outcome.Succeeded ? HuggingFaceEnvironment.Read(outcome.StandardOutput) : null;
    }

    private HuggingFaceLocations Documented(VariableReading reading) =>
        HuggingFaceLocations.Resolve(reading, Environment, _system, Volumes);

    private static PlanNote Information(string message) => new(PlanNoteSeverity.Information, message);
}
