using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Electron runtimes <c>@electron/get</c> downloads for every project that installs Electron, one
/// zip per version, platform and architecture (150.9 MB on the machine this was measured on, for one
/// version). Nothing ever removes one.
///
/// <para><b>Where it is.</b> <c>@electron/get</c>'s <c>Cache.ts</c> roots the cache at
/// <c>envPaths('electron', { suffix: '' }).cache</c>, and env-paths builds that on Windows as
/// <c>path.join(localAppData, name, 'Cache')</c>, so <c>%LOCALAPPDATA%\electron\Cache</c>. The
/// <c>electron</c> package's install script passes <c>cacheRoot: process.env.electron_config_cache</c>,
/// which is the one setting that moves it (electron/electron <c>docs/tutorial/installation.md</c>). It
/// is resolved against the working directory of that install script, which Deguffer is not, so a
/// relative value is declined.</para>
///
/// <para><b>What is in it.</b> <c>Cache.getCacheDirectory</c> names each entry by
/// <c>crypto.createHash('sha256').update(url).digest('hex')</c> of the download address with its file
/// name, query and fragment removed: always 64 lowercase hexadecimal characters. The entry holds the
/// zip, and in releases before 4.0 the release's <c>SHASUMS256.txt</c> beside it. Older releases of
/// <c>@electron/get</c> and the <c>electron-download</c> package before it wrote other layouts here
/// (zips directly in the root, folders named after the whole address, <c>tmp-*</c> downloads). Those
/// are not recognised, and stay Tier 4.</para>
///
/// <para><b>§5.1 was asked and the answer is no.</b> Neither <c>@electron/get</c> nor the
/// <c>electron</c> package has a command that clears the cache, so this is the path-based case. An
/// installed project is untouched: the zip is only unpacked into its <c>node_modules</c>, and the cost
/// is one download at the next fresh install of that version.</para>
/// </summary>
public sealed partial class ElectronCacheProvider : CleanupProviderBase
{
    /// <summary>Set by the user, or by npm from its own configuration, to move the cache.</summary>
    public const string CacheVariable = "electron_config_cache";

    /// <summary>
    /// One cached download: the SHA-256 of its address, as Node writes a hexadecimal digest. Anything
    /// else in the cache is not recognised.
    /// </summary>
    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RecognisedChild();

    private const string UnrecognisedReason =
        "Not named the way Electron's downloader names a cached download, so it is left alone.";

    private readonly ISystemDirectories _system;

    public ElectronCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
    }

    public override string Id => "electron";

    public override string Name => "Electron downloads";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    /// <summary>Each download is one Electron version for one platform.</summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "The next time a project installs one of these Electron versions from scratch, it downloads it "
        + "again. Projects already installed keep working, because each has its own unpacked copy.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "Electron, the framework many desktop applications are built with",
        Publisher = "the OpenJS Foundation's Electron project",
        Purpose = "Each time a project installs Electron, its downloader keeps a copy of the runtime it "
            + "fetched, one zip of around a hundred megabytes for each version, platform and "
            + "architecture. Nothing ever removes one, so they build up with every version a project "
            + "has used.",
        Recommendation = "Nothing here is yours: every entry is a download Electron's installer fetches "
            + "again when a project next needs that version. Projects already installed keep their own "
            + "unpacked copy.",
    };

    /// <summary>
    /// None. The downloader runs inside an npm install, as Node, and a warning on every Node process
    /// would fire for anything at all. A download is moved into place whole by a rename, so a clean
    /// beside one removes either the old entry or nothing.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => [];

    /// <summary>Where the downloader keeps its cache when <see cref="CacheVariable"/> is unset.</summary>
    public string DefaultRoot => Path.Combine(Environment.LocalAppData, "electron", "Cache");

    /// <summary>
    /// The cache, honouring <see cref="CacheVariable"/>, or null where that names nothing this can
    /// examine as Electron's. See <see cref="ConfiguredFolder"/>.
    /// </summary>
    public string? ResolveRoot() => Resolve().Folder;

    public override IReadOnlyList<ToolRoot> ToolRoots =>
        ResolveRoot() is { } root
            ?
            [
                ToolRoot.Folders(
                    root,
                    "This is Electron's download cache. Deguffer removes whole cached downloads from it, "
                    + "each in a folder named by a long hexadecimal code, and nothing else.",
                    name => RecognisedChild().IsMatch(name)),
            ]
            : [];

    /// <summary>
    /// The cache the variable names and the default one, because the variable can be unset again after
    /// a duplicate search has read it. Each whole: a download is any folder with a 64-character name,
    /// which no narrower place can name.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            .. new[] { ResolveRoot(), DefaultRoot }
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(CleanedPlace.Whole),
        ]);

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ResolveRoot() is { } root && LongPath.DirectoryMayExist(root));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var setting = Resolve();

        if (setting.Folder is not { } root)
        {
            return EmptyPlan(
                $"{CacheVariable} is set to '{setting.Value}', and Deguffer will not treat that as Electron's "
                + $"cache: {setting.Declined} It is leaving it alone.");
        }

        if (NothingToPlanFor(root, $"Electron has not downloaded anything for this user — no {root} directory.") is { } nothing)
        {
            return nothing;
        }

        // The root arrives by name, so nothing has classified it. A junctioned root hands back the far
        // side's ordinary directories, and every survivor named for this root would resolve through
        // the same link and pass.
        if (LongPath.IsReparsePoint(root))
        {
            return UnexaminedPlan(
                $"Leaving '{root}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        var notes = new List<PlanNote>();
        var spared = new List<(string Path, string Reason)>();
        var targets = new List<DeletionTarget>();

        var scan = ChildDirectories.Under(root);

        if (scan.Unreadable)
        {
            notes.Add(UnreadableRoot.Note(root));
        }

        foreach (var link in scan.Links)
        {
            var path = LongPath.Display(link.FullName);

            notes.Add(CacheLevelWalk.Note(path));
            spared.Add((path, CacheLevelWalk.LinkReason));
        }

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(child.FullName);

            if (!RecognisedChild().IsMatch(child.Name))
            {
                var survivor = (path, UnrecognisedReason);

                notes.Add(CacheLevelWalk.SparedNote(survivor));
                spared.Add(survivor);
                continue;
            }

            var downloads = Downloads(child);

            targets.Add(new DeletionTarget(
                path,
                downloads.Count > 0
                    ? $"Electron download {string.Join(", ", downloads.Select(d => $"'{d}'"))}, fetched again by the next install that needs it."
                    : "An Electron download folder, fetched again by the next install that needs it.",
                Identity: new ItemIdentity(child.Name, downloads.Count > 0 ? downloads[0] : child.Name),
                Facets: [.. downloads.Select(download => new ItemFacet("Download", download))]));
        }

        if (targets.Count == 0 && spared.Count == 0 && !scan.Unreadable)
        {
            return EmptyPlan($"Electron's download cache in {root} is empty.");
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

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
            ProtectedPaths = Protect(
            [
                (root, "Electron's cache folder itself must survive — only recognised downloads inside it are removed."),
                .. spared,
            ]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = scan.Unreadable,
            WasNotExamined = targets.Count == 0 && spared.Count > 0,
        };
    }

    /// <summary>
    /// The zips in one cached download, so the user sees the version rather than a hexadecimal name.
    /// Read from the folder's own listing, and empty where it will not be listed: the folder is offered
    /// on its name either way, and only its label is lost.
    /// </summary>
    private static IReadOnlyList<string> Downloads(DirectoryInfo entry)
    {
        try
        {
            return [.. entry.EnumerateFiles("*.zip").Select(file => file.Name).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private ConfiguredFolder.Setting Resolve() =>
        ConfiguredFolder.FromVariable(CacheVariable, DefaultRoot, Environment, _system, Volumes);
}
