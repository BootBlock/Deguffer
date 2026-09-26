using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The media cache Premiere Pro, After Effects, Audition and Media Encoder share: the audio they
/// converted from each clip they imported (<c>.cfa</c>), the waveforms they drew (<c>.pek</c>), what
/// they read from each file, and the database that indexes them. Adobe keeps all of it until the user
/// deletes it, and a community report puts one machine's at 52.5 GB.
///
/// <para><b>Tier 2, not the Tier 1 the files' nature suggests.</b> Every file is derived from footage
/// still on disk, and Adobe re-creates each as the footage is next used. The refill is converting and
/// analysing every clip of every project again as it opens, and Adobe warns of the delay: that is
/// §3's "re-indexing for minutes". Premiere Pro keeps its media search analysis in the same cache, so
/// that is redone too, and footage on a drive that is not connected cannot be converted until it
/// is.</para>
///
/// <para><b>§5.1 exists and cannot be driven.</b> Adobe's route is in Premiere Pro under <b>Edit →
/// Preferences → Media Cache</b>, whose <b>Delete</b> removes unused cache files or all of them, and a
/// preference that deletes old files, which runs ten minutes after the application starts and then
/// weekly. None of it is anything a cleanup tool can call and read an answer from. Adobe's own manual
/// route is deleting these folders with the application closed, which is what this does.</para>
///
/// <para><b>Nothing while an Adobe application runs (§5.3).</b> Adobe says to close the application
/// first, and the database is open while it runs. So while any of
/// <see cref="AdobeMediaCacheLayout.ProcessNames"/> is running nothing is offered and Explore refuses
/// the cache, and the clean asks again before each removal.</para>
///
/// <para>Where the cache is, and what beside it is never reached, is <see cref="AdobeMediaCacheLayout"/>.</para>
/// </summary>
public sealed class AdobeMediaCacheProvider : CleanupProviderBase
{
    private const string HeldReason =
        "An Adobe application that uses the media cache is running, and Adobe says to close it before its "
        + "cache is deleted, so the cache is left alone. Close Premiere Pro, After Effects, Audition and "
        + "Media Encoder, then scan again.";

    private const string ExploreReason =
        "This is Adobe's shared folder. Your LUTs, motion graphics templates and Team Projects' auto-saves "
        + "are in it, and only the media cache folders in it may be removed.";

    private readonly IVolumeInventory _volumes;
    private readonly RunningProcessCheck _stillClosed;
    private AdobeMediaCacheLayout? _layout;
    private DeclaredLocationScan? _scan;

    public AdobeMediaCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _volumes = volumes ?? VolumeInventory.Current;
        _stillClosed = new RunningProcessCheck(Inspector, AdobeMediaCacheLayout.ProcessNames);
    }

    public override string Id => "adobe-media-cache";

    public override string Name => "Adobe media cache";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Premiere Pro, After Effects, Audition and Media Encoder convert the audio and draw the waveforms "
        + "of your footage again as each project opens, so a large project opens slowly once, and Premiere "
        + "Pro analyses footage for its media search again. Footage on a drive that is not connected is "
        + "converted once the drive is back. Your projects, footage, LUTs, templates and Team Projects are "
        + "untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Premiere Pro, After Effects, Audition and Media Encoder",
        Publisher = "Adobe",
        Purpose = "Adobe's video and audio applications convert the audio of every clip you import and "
            + "draw its waveform, so that it plays and shows at once, and keep the results in a media cache "
            + "they share. Adobe keeps it until you delete it.",
        Recommendation = "Premiere Pro can delete its own media cache under Edit, Preferences, Media Cache. "
            + "Deguffer removes the same folders, wherever Adobe's settings put them, and only while every "
            + "Adobe application is closed.",
    };

    /// <summary>
    /// Where Adobe keeps its cache, read once per planning pass (G4). Presence, planning and Explore's
    /// refusals all ask the same question of the same settings.
    /// </summary>
    private AdobeMediaCacheLayout Layout => _layout ??= AdobeMediaCacheLayout.Find(Environment, _volumes);

    /// <summary>The one look at the disk presence and the plan both read, kept for a planning pass.</summary>
    private DeclaredLocationScan Scan(CancellationToken ct) => _scan ??= DeclaredLocations.Examine(Layout.Roots, ct);

    public override void InvalidateCaches()
    {
        _layout = null;
        _scan = null;
        _volumes.Invalidate();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Present where a cache folder is there, could not be read, or is one Adobe's settings name and
    /// Deguffer left alone. <c>%APPDATA%\Adobe\Common</c> alone is not presence: it also holds what this
    /// row never removes.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(!Scan(ct).FoundNothing || Layout.Notes.Count > 0);

    /// <summary>
    /// §5.2 as §7.1 reads it. Adobe's shared folder recognises only its cache folders, and while an
    /// Adobe application runs, none, and nothing inside any cache folder may go either.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var common = AdobeMediaCacheLayout.DefaultFolder(Environment);
        var running = AdobeIsRunning();
        var recognised = Layout.Roots
            .Where(root => root.Path.Equals(common, StringComparison.OrdinalIgnoreCase))
            .SelectMany(root => root.Locations)
            .Select(location => location.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            running
                ? new ToolRoot(common, HeldReason, static _ => false)
                : new ToolRoot(common, ExploreReason, recognised.Contains),
            .. CacheFolders()
                .Where(_ => running)
                .Select(folder => new ToolRoot(folder, HeldReason, static _ => false)),
        ]);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var layout = Layout;
        var scan = Scan(ct);

        if (scan.FoundNothing && layout.Notes.Count == 0)
        {
            return EmptyPlan("Adobe's video and audio applications have left no media cache in this user's account.");
        }

        var notes = new List<PlanNote>(scan.Notes);
        notes.AddRange(layout.Notes);

        var held = AdobeIsRunning() ? scan.Targets.Select(target => target.Path).ToList() : [];

        if (held.Count > 0)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Warning, $"Left the media cache alone: {HeldReason}"));
        }

        var (steps, measured) = await PlanDeletionsAsync(
            [.. scan.Targets.Where(_ => held.Count == 0).Select(target => target with { UseCheck = _stillClosed })],
            keep,
            ct).ConfigureAwait(false);

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
            // Held first, so a held cache folder is named for why it was held rather than as the
            // folder only the contents of are removed.
            ProtectedPaths = Protect(
            [
                .. held.Select(path => (Path: path, Reason: HeldReason))
                    .Concat(scan.Protected)
                    .DistinctBy(survivor => survivor.Path, StringComparer.OrdinalIgnoreCase),
            ]),
            Notes = notes,
            Fallback = measured.Fallback,
            // A cache held back while Adobe runs, behind a link, or where the settings could not be
            // followed is something real left unexamined, so a row with no steps must not read as clear.
            WasNotExamined = steps.Count == 0 && (held.Count > 0 || scan.Declined.Count > 0 || layout.LeftSomethingUnexamined),
            HasUnreadableRoot = scan.CouldNotBeReached,
        };
    }

    private IEnumerable<string> CacheFolders() =>
        from root in Layout.Roots
        from location in root.Locations
        select Path.Combine(root.Path, location.RelativePath);

    private bool AdobeIsRunning() => Inspector.FindRunning(AdobeMediaCacheLayout.ProcessNames).Count > 0;
}
