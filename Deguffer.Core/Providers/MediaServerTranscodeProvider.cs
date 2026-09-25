using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The segments a media server's transcoder writes while it converts a film or an episode for a
/// player that cannot play the original, and leaves behind when a stream ends badly.
///
/// <para><b>Tier 1.</b> Every segment is derived from media still on disk, and the cost of losing one
/// is converting it again the next time it is played.</para>
///
/// <para><b>A floor of <see cref="QuietHours"/> hours, because a live segment and a dead one look the
/// same.</b> A server keeps running while nobody is at the machine, and a stream playing now is
/// writing into the same folder its dead predecessors sit in. Deleting a live segment
/// ends somebody's film part-way through. Jellyfin's own clean-up task waits a day before it deletes
/// a file, so the floor is its figure, applied to all three. It travels on the plan, so a segment
/// written between the preview and the clean is left where it is.</para>
///
/// <para><b>§5.1 has a route in every server, and Deguffer can take none of them.</b> Jellyfin and
/// Plex each run a clean-up task that an administrator's sign-in can start over the network, and Emby
/// clears its folder when it starts. Deguffer holds no sign-in and must not stop a server, so it
/// removes the files by name, from folders it names outright.</para>
///
/// <para>Each server's own knowledge is in its <see cref="MediaServerLayout"/>. This class holds only
/// the plan the three share.</para>
/// </summary>
public abstract class MediaServerTranscodeProvider : CleanupProviderBase
{
    /// <summary>How long a file must have gone unwritten before it is offered.</summary>
    public const int QuietHours = 24;

    private MediaServerLayout? _layout;

    protected MediaServerTranscodeProvider(
        IUserEnvironment environment,
        IProcessRunner runner,
        IProcessInspector inspector,
        IDirectoryScanner scanner)
        : base(environment, runner, inspector, scanner)
    {
    }

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    /// <summary>
    /// Where this server keeps its files, read once per planning pass (G4). Presence, planning and the
    /// §5.2 declarations all ask the same question of the same settings.
    /// </summary>
    public MediaServerLayout Layout => _layout ??= FindLayout();

    /// <summary>What this provider names. Exposed so tests can assert the declaration itself.</summary>
    public IReadOnlyList<DeclaredRoot> Roots => Layout.Roots;

    public override IReadOnlyList<ToolRoot> ToolRoots => Layout.ToolRoots;

    /// <summary>What the user is told when this server has left nothing on the machine.</summary>
    protected abstract string NothingHere { get; }

    /// <summary>The server's name, as the sentence about the floor uses it.</summary>
    protected abstract string ServerName { get; }

    /// <summary>Read this server's settings and say where its transcoder's files are.</summary>
    protected abstract MediaServerLayout FindLayout();

    public override void InvalidateCaches()
    {
        _layout = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// Presence is a folder the transcoder writes to, or a sentence owed about the settings. The server's
    /// own folder is not enough: it also holds everything this row never removes.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var layout = Layout;

        return Task.FromResult(
            layout.Notes.Count > 0
            || layout.Roots.Any(root => root.Locations.Any(location =>
                LongPath.DirectoryMayExist(Path.Combine(root.Path, location.RelativePath)))));
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var layout = Layout;
        var scan = DeclaredLocations.Examine(layout.Roots, ct);

        if (scan.FoundNothing && layout.Notes.Count == 0)
        {
            return EmptyPlan(NothingHere);
        }

        // Fixed once, here, so the preview and the removal agree about which segments are recent however
        // long the preview sits on screen. See WindowsUpdateLeftoverProvider.
        var floor = MinimumAge.Within(TimeSpan.FromHours(QuietHours), DateTime.UtcNow);
        var effective = MinimumAge.Stricter(keep, floor);

        var notes = new List<PlanNote>(scan.Notes);
        notes.AddRange(layout.Notes);

        var (steps, measured) = await PlanDeletionsAsync(scan.Targets, effective, ct).ConfigureAwait(false);

        if (steps.Count > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving anything {ServerName} wrote in the last {floor.Describe()} alone, so a film or "
                + "an episode playing now keeps the files it is playing from."));
        }

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        // §5.3, and only where something is going to be removed.
        if (steps.Count > 0 && BuildRunningProcessNote() is { } warning)
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
            ProtectedPaths = Protect(
                [.. scan.Protected.Concat(layout.Survivors).DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase)]),
            Notes = notes,
            Fallback = measured.Fallback,
            WasNotExamined = scan.Targets.Count == 0 && (scan.Declined.Count > 0 || layout.LeftSomethingUnexamined),
            HasUnreadableRoot = scan.CouldNotBeReached,

            // The floor travels on the plan. CleanupProviderBase.PlanAsync will not loosen it.
            Keep = floor,
        };
    }
}
