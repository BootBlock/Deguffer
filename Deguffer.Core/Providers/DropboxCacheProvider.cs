using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The hidden <c>.dropbox.cache</c> at the top of every Dropbox folder, which Dropbox describes as "a
/// staging area for downloading and uploading files" and clears by itself every three days.
///
/// <para><b>Researched, not measured.</b> No machine this was written against has Dropbox installed.
/// The cache is normally small because of that purge, and grows when the client cannot finish
/// syncing, which is the case this row is for. Where the folders are is
/// <see cref="DropboxFolders"/>.</para>
///
/// <para><b>Tier 2, emptied in place.</b> Dropbox's own route is manual: quit the app fully, then move
/// the files in the folder to the Recycle Bin. That is what this does, so the folder itself and
/// everything beside it stay (§5.2), and §5.6 asserts each. Dropbox stages uploads here as well as
/// downloads, and nothing published says that a staged upload is never the only copy of a change, so
/// the row is never ticked.</para>
///
/// <para><b>Nothing while Dropbox runs (§5.3).</b> The vendor's steps start with quitting it, so while
/// <c>Dropbox</c> is in the process table each cache is left alone, named as a survivor and refused in
/// Explore, and the clean asks again before each cache is emptied.</para>
/// </summary>
public sealed class DropboxCacheProvider : CleanupProviderBase
{
    private static readonly IReadOnlyList<string> ProcessNames = [DropboxFolders.ProcessName];

    private const string HeldReason =
        "Dropbox is running, and it stages the files it downloads and uploads in this folder, so the cache "
        + "is left alone. Quit Dropbox from its icon in the taskbar, then scan again.";

    private const string CacheReason =
        "Files Dropbox staged while it downloaded and uploaded, which it clears by itself every three days";

    private const string FolderReason =
        "Your Dropbox folder, with your files in it. Only what is inside its .dropbox.cache folder is removed.";

    private const string BesideReason =
        "In your Dropbox folder, beside Dropbox's cache. It is never removed.";

    private const string RecordReason =
        "Dropbox's record of where its folders are. It is never removed.";

    private const string ExploreReason =
        "This is Dropbox's cache folder. What is inside it may be removed, but not the folder itself.";

    private const string DeclinedExploreReason =
        "Dropbox records this as a Dropbox folder. Deguffer does not treat it as one, so only Dropbox's "
        + "cache in it is refused.";

    private readonly ISystemDirectories _system;
    private readonly RunningProcessCheck _stillClosed;
    private readonly PassMemo<DropboxFolders> _folders = new();

    public DropboxCacheProvider(
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
        _stillClosed = new RunningProcessCheck(Inspector, ProcessNames);
    }

    public override string Id => "dropbox-cache";

    public override string Name => "Dropbox cache";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Parts;

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override string WhatHappensOnNextUse =>
        "Dropbox downloads again anything it had staged here and still needs. Your Dropbox folder, the files "
        + "in it, your settings and your account are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.TemporaryFiles,
        Application = "Dropbox, the file sync app, and its desktop app for Windows",
        Publisher = "Dropbox, Inc.",
        Purpose = "Every Dropbox folder holds a hidden .dropbox.cache folder, where Dropbox stages the files "
            + "it downloads and uploads. Dropbox clears it every three days, but it can grow large while the "
            + "app cannot finish syncing.",
        Recommendation = "Dropbox's own advice is to quit the app and delete what is in this folder. Deguffer "
            + "empties it and keeps the folder itself, and removes nothing while Dropbox is running. It is not "
            + "selected by default, because Dropbox stages uploads here as well as downloads.",
    };

    private DropboxFolders Folders => _folders.Get(() => DropboxFolders.Find(Environment, _system, Volumes));

    /// <summary>Every answer here is a record or the disk, which can change between one scan and the next.</summary>
    public override void InvalidateCaches()
    {
        _folders.Forget();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Present where a record names a Dropbox folder that is there or may be, or where a record could
    /// not be read or named a folder that was declined.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var folders = Folders;

        return Task.FromResult(folders.Folders.Count > 0 || folders.LeftSomethingUnexamined);
    }

    /// <summary>
    /// §5.2 for Explore. Each cache recognises everything inside it, because the clean empties it, and
    /// is itself refused. While Dropbox runs it recognises nothing. A declined folder refuses only the
    /// cache Dropbox writes in it.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var folders = Folders;
        var running = IsRunning();

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. folders.Folders.Select(folder => running
                ? new ToolRoot(folder.Cache, HeldReason, static _ => false)
                : new ToolRoot(folder.Cache, ExploreReason, static _ => true)),
            .. folders.DeclinedFolders.Select(folder => ToolRoot.Sparing(folder, DeclinedExploreReason, [DropboxFolders.CacheFolder])),
        ]);
    }

    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([.. Folders.Folders.Select(folder => CleanedPlace.Whole(folder.Cache))]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var folders = Folders;

        if (folders.Folders.Count == 0 && folders.Notes.Count == 0)
        {
            return EmptyPlan("Dropbox is not set up in this user's account.");
        }

        var scan = DeclaredLocations.Examine(
            [
                .. folders.Folders.Select(folder => new DeclaredRoot(
                    folder.Path,
                    FolderReason,
                    RequiresElevation: false,
                    [new DeclaredLocation(DropboxFolders.CacheFolder, CacheReason, DeclaredLocationKind.DirectoryContents, ReportsAge: false)],
                    ProtectedNames: [])),
            ],
            ct);

        // A cache Dropbox has just cleared holds nothing to offer.
        var found = scan.Targets.Where(target => DirectoryContent.MayBePresent(target.Path)).ToList();
        var notes = new List<PlanNote>(scan.Notes);
        notes.AddRange(folders.Notes);

        var held = IsRunning() ? found.Select(target => target.Path).ToList() : [];

        if (held.Count > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left Dropbox's cache alone: {HeldReason}"));
        }

        var offered = held.Count > 0 ? [] : found;

        var (steps, measured) = await PlanDeletionsAsync(
            [.. offered.Select(target => target with { UseCheck = _stillClosed })],
            keep,
            ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        if (found.Count == 0 && scan.Declined.Count == 0 && !scan.CouldNotBeReached && !folders.LeftSomethingUnexamined)
        {
            notes.Insert(0, new PlanNote(PlanNoteSeverity.Information, "Dropbox's cache holds nothing."));
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            // Held first, so a held cache is named for why it was held rather than as the folder only the
            // contents of are removed.
            ProtectedPaths = Protect(
            [
                .. held.Select(path => (Path: path, Reason: HeldReason)),
                .. scan.Protected,
                .. offered.SelectMany(target => Beside(target.Path)),
                .. folders.Records.Select(record => (Path: record, Reason: RecordReason)),
            ]),
            Notes = notes,
            Fallback = measured.Fallback,
            // A cache held back while Dropbox runs, or one behind a link, unread or declined, is something
            // real left unexamined, so a row with no steps must not read as clear.
            WasNotExamined = steps.Count == 0 && (held.Count > 0 || scan.Declined.Count > 0 || folders.LeftSomethingUnexamined),
            HasUnreadableRoot = scan.CouldNotBeReached,
        };
    }

    /// <summary>
    /// §5.6's unrecognised siblings: every entry at the top of the Dropbox folder holding
    /// <paramref name="cache"/>, other than the cache. The emptied check on the Dropbox folder itself
    /// cannot stand in for them, because a target inside it exempts it.
    /// </summary>
    private static IEnumerable<(string Path, string Reason)> Beside(string cache)
    {
        var folder = Path.GetDirectoryName(cache)!;

        return (FolderEntries.Of(folder) ?? [])
            .Where(entry => !entry.Name.Equals(DropboxFolders.CacheFolder, StringComparison.OrdinalIgnoreCase))
            .Select(entry => (Path.Combine(folder, entry.Name), BesideReason));
    }

    private bool IsRunning() => Inspector.FindRunning(ProcessNames).Count > 0;
}
