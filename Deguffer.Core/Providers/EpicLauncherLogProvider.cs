using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The logs and crash reports the Epic Games launcher writes about itself (58 MB of crash reports
/// and 0.7 MB of logs on the measured machine).
///
/// <para><b>Tier 3, on <see cref="CrashDumpProvider"/>'s reasoning.</b> §3's Tier 1 requires that
/// whatever produced the content re-creates it, so that nothing is lost. What is re-created here is
/// the <em>next</em> log, never the ones removed: a crash report is the record of an event, and the
/// event will not happen again to order. §3 names logs and records in its Tier 3 row, and the
/// consequence column says the loss is permanent, which is exactly right. Somebody halfway through
/// a support ticket has the only copy of the evidence in this folder. So the row stays unticked and
/// each one carries the newest write inside it, and the decision stays theirs.</para>
///
/// <para>Separate from <see cref="EpicLauncherWebCacheProvider"/> rather than one provider over
/// both, for the reason <see cref="EpicLauncherSaved"/> gives: they are two tiers, and a plan
/// carries one. It is the same split <see cref="WindowsServicingLogProvider"/> made against the
/// crash dumps, and it lets somebody clear 343 MB of web cache without touching the evidence of a
/// crash.</para>
///
/// <para>§5.1 does not apply. The launcher offers no way to clear either folder, and Epic's own
/// support material treats them as ordinary directories to delete.</para>
///
/// <para><b>No age filter, deliberately.</b> A report written this morning may be the only evidence
/// in a ticket somebody is still writing. The answer to that is the tier and the age column rather
/// than a cut-off, which would take the decision away and would also have to change the grain from
/// one folder to one report.</para>
/// </summary>
public sealed class EpicLauncherLogProvider : CleanupProviderBase
{
    private const string LinkReason =
        "A link rather than a directory, so what it points at was never classified.";

    public EpicLauncherLogProvider(
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

    public override string Id => "epic-launcher-logs";

    public override string Name => "Epic Games launcher logs and crash reports";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override string WhatHappensOnNextUse =>
        "The record of every crash and every session the launcher has already had is destroyed, so "
        + "none of it can be attached to a support ticket afterwards. The launcher writes a fresh "
        + "log the next time it starts, and nothing about how it runs changes.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Epic Games launcher",
        Publisher = "Epic Games",
        Purpose = "The launcher writes a log every time it or its updater runs, and gathers a full "
            + "crash report whenever it fails. Neither folder is ever trimmed, so on a machine that "
            + "has run the launcher for years the crash reports alone can be tens of megabytes.",
        Recommendation = "Nothing re-creates a crash report: it is the record of an event that will "
            + "not happen again to order, and anyone in the middle of a support ticket has the only "
            + "copy here. The row stays unticked and shows how recently each folder was written to.",
    };

    /// <summary>§5.3. The launcher holds the log it is currently writing open.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => EpicLauncherSaved.ProcessNames;

    /// <summary>The launcher's <c>Saved</c> folder. Exposed so tests can assert it is never a target.</summary>
    public string SavedPath => EpicLauncherSaved.PathIn(Environment);

    /// <summary>
    /// Presence is one of the two declared folders actually being there, probed by name rather than
    /// by enumerating (G4). The <c>Saved</c> folder exists on every machine that has opened the
    /// launcher, so reading it as a hit would report this source and then plan nothing.
    ///
    /// <para>A link on the way down counts as present, because the sentence saying what Deguffer
    /// declined to look at lives in the plan, and the planner never asks an absent provider for
    /// one.</para>
    ///
    /// <para><b>A presence probe, not a safety gate.</b> It answers through a junction, so a folder
    /// that is a link reports as present here and then yields no target, because
    /// <see cref="BuildPlanAsync"/> declines it.</para>
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        if (EpicLauncherSaved.FirstLinkTo(Environment) is not null)
        {
            return Task.FromResult(true);
        }

        var saved = SavedPath;

        foreach (var name in EpicLauncherSaved.Diagnostics.DisposableNames)
        {
            ct.ThrowIfCancellationRequested();

            if (LongPath.DirectoryExists(Path.Combine(saved, name)))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    /// <summary>§5.2 as §7.1 needs it read from outside. The declaration is the shared one.</summary>
    public override IReadOnlyList<ToolRoot> ToolRoots => [EpicLauncherSaved.Root(Environment)];

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        // Before the existence check, not after it. A link partway up resolves onto a directory that
        // holds no launcher folder of its own, so Saved reads as absent and the pass would end
        // reporting nothing at all about a redirection it did detect.
        if (EpicLauncherSaved.FirstLinkTo(Environment) is { } link)
        {
            return UnexaminedPlan(
                $"Leaving '{link}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        var saved = SavedPath;

        if (!LongPath.DirectoryExists(saved))
        {
            return EmptyPlan("The Epic Games launcher has kept no folder in this user's account.");
        }

        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();
        var declined = new List<(string Path, string Reason)>();

        var survivors = new List<(string Path, string Reason)>
        {
            (saved, "The launcher's own folder must survive — only its logs and crash reports are removed."),
        };

        survivors.AddRange(EpicLauncherSaved.ProtectedNames.Select(
            n => (Path.Combine(saved, n.Name), n.Reason)));

        var scan = ChildDirectories.Under(saved);

        // The folder was found on disk by name above, and a listing right is separate from a
        // traverse right — so a refusal here leaves a plan with no steps and, without this, nothing
        // said. The shell renders that as "Already clear", which is a claim about a folder nobody
        // read.
        if (scan.Unreadable)
        {
            notes.Add(UnreadableRoot.Note(saved));
        }

        // Only the links this provider would otherwise have removed. A link named 'webcache_4430'
        // is a child of the same folder and is the web cache row's subject, not this one's, and
        // naming it here would put a sentence about somebody else's row in front of the user.
        foreach (var linked in scan.Links.Where(l => EpicLauncherSaved.Diagnostics.IsDisposable(l.Name)))
        {
            var path = LongPath.Display(linked.FullName);

            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving '{path}' alone: it is a link to somewhere else, and Deguffer does not "
                + "delete through a link."));

            declined.Add((path, LinkReason));
        }

        var spared = 0;

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var classification = EpicLauncherSaved.Classify(child.Name);
            var path = LongPath.Display(child.FullName);

            if (!classification.Tier.IsOfferable())
            {
                survivors.Add((path, classification.Reason));
                spared++;
                continue;
            }

            // §7's age, from the newest write one level down. A log is appended to, which moves the
            // file and leaves the parent alone, so the folder's own timestamp would report a log
            // being written this minute as months old.
            targets.Add(new DeletionTarget(path, classification.Reason, DirectoryAge.Of(child.FullName, ct)));
        }

        // One note rather than one per spared child. Each is still asserted individually by §5.6,
        // and this is the sentence that says so.
        if (spared > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"{spared} other {(spared == 1 ? "item is" : "items are")} left alone beside the "
                + "logs. Your launcher settings, your cloud saves and the store's own data are in "
                + "that folder."));
        }

        if (targets.Count == 0 && declined.Count == 0 && !scan.Unreadable)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "The Epic Games launcher is holding no logs or crash reports on this machine."));
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        // §5.3, and only where something is actually going to be removed. A warning that the
        // launcher is holding its log open, on a row with nothing to delete, describes a clean that
        // will not happen.
        if (targets.Count > 0 && BuildRunningProcessNote() is { } warning)
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
                [.. survivors.Concat(declined).DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase)]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = scan.Unreadable,
            WasNotExamined = targets.Count == 0 && declined.Count > 0,
        };
    }
}
