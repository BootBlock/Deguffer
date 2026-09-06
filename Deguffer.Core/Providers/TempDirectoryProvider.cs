using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The scratch folders every program on the machine writes to and forgets: this account's own, and
/// the one Windows and its services share (5.4 GB across the two on the audited machine).
///
/// <para><b>§5.3 is the whole of this provider, and it says temporary is not free real estate.</b>
/// Blanket-clearing a temporary folder is named there as the classic mistake, and the measurement
/// behind that sentence is specific: during the founding audit an active session held 344 MB of
/// live working files in <c>%TEMP%</c>, with dozens of processes holding handles open inside it.
/// Three requirements follow, and all three are met here rather than in the shell.</para>
///
/// <list type="number">
/// <item><b>An age filter, and one Deguffer imposes rather than one the user sets.</b> Nothing older
/// than <see cref="StaleAfter"/> is offered, whatever the guard on recently changed files is set to
/// — the plan carries that as its own <see cref="CleanupPlan.Keep"/>, and
/// <see cref="MinimumAge.Stricter"/> is what stops a user's shorter window loosening it. Seven days
/// is what Windows' own Disk Cleanup applies to these two folders, so it is the interval a machine
/// already behaves as though it had.</item>
/// <item><b>Exclusion of what a running program is using.</b> An entry a process is running from or
/// working in is never a target, however old its files are: the process holding it may have opened
/// nothing this minute, so nothing is locked and the timestamps prove nothing.
/// <see cref="ILiveTreeInspector.FindLiveChildren"/> answers that in one pass over the process
/// table, and each entry it names is asserted to have survived (§5.6).</item>
/// <item><b>Access denied treated as ordinary.</b> A locked file is Windows protecting live state,
/// so <see cref="DirectoryRemover"/> counts it and moves on, which is what it already does.</item>
/// </list>
///
/// <para><b>The folder itself stays, and that is not a nicety.</b> Every program on the machine
/// expects <c>%TEMP%</c> to be there, and Windows does not put it back — a profile whose scratch
/// folder has been deleted is one where the next installer fails for a reason nobody will connect to
/// a disk cleaner. So this provider plans <see cref="ClearDirectoryStep"/>s rather than deletions,
/// and §5.6 asserts each folder is still standing afterwards.</para>
///
/// <para><b>Tier 2, and §4.2's table proposed Tier 1.</b> The correction is worth stating rather
/// than making quietly. Tier 1's promise is that nothing is lost, because whatever produced the
/// content re-creates it on demand. Nothing re-creates a temporary file: it is what a program left
/// behind, and the program has gone. What makes the row safe to offer at all is the age filter and
/// the live-process exclusion above, and neither of those is a claim that the content is
/// regenerable — they are a claim that it is abandoned, which is a different and weaker thing. The
/// practical difference between the two tiers is whether the row is ticked before the user has read
/// it, and on the folder §5.3 calls the classic mistake it should not be.</para>
///
/// <para><b>§5.1 is answered rather than skipped, and the answer is the one the crash dumps
/// gave.</b> Windows does ship a route — Disk Cleanup registers a <c>VolumeCaches</c> handler for
/// temporary files, and Storage Sense clears the same folder on a schedule — and neither is used
/// here. Selecting a handler means writing <c>StateFlags</c> into the machine's own registry, which
/// is a change to the user's Disk Cleanup configuration made on their behalf, and the run then
/// reports nothing back. Storage Sense is a setting rather than a command, and switching it on is
/// not Deguffer's to do. This plan names each folder with a size, states the cut-off it is applying,
/// and asserts what survived beside it, none of which a call returning one number for the volume
/// could support.</para>
/// </summary>
public sealed class TempDirectoryProvider : CleanupProviderBase
{
    /// <summary>
    /// How long something must have sat untouched before this provider will offer it.
    ///
    /// <para>Seven days, which is §5.3's own suggestion and is also what Windows applies to these
    /// two folders through Disk Cleanup and Storage Sense. Matching it means Deguffer offers what
    /// the machine would already have removed on its own, rather than inventing a threshold nothing
    /// else on the system agrees with.</para>
    ///
    /// <para>Public because it is the number the row's own sentences quote, and a constant quoted in
    /// prose is one a test can hold the prose to.</para>
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    private readonly ILiveTreeInspector _liveTrees;
    private readonly ISystemDirectories _system;
    private TempRootSet? _roots;

    public TempDirectoryProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        ILiveTreeInspector? liveTrees = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _system = system ?? SystemDirectories.Current;
        _liveTrees = liveTrees ?? LiveTreeInspector.Default;
    }

    public override string Id => "temp-directories";

    public override string Name => "Temporary files";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "Nothing that is running is affected. Everything offered here was last touched more than "
        + "seven days ago, and anything a running program is working in is left where it is. What "
        + "you lose is whatever a program stored in a temporary folder and still expects to find "
        + "there, which installers and crash reporters occasionally do.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Windows, and every program on the machine that writes scratch files",
        Publisher = "Microsoft, and whichever program left each file behind",
        Purpose = "Installers, compilers, browsers and test runners unpack and scratch in a "
            + "temporary folder and are meant to clear up afterwards. A great many never do, so "
            + "both folders grow without limit — this account's own, and the one Windows and its "
            + "services share.",
        Recommendation = "Live working files sit among abandoned ones and look identical, so "
            + "Deguffer offers only what nothing has touched for a week and leaves alone anything a "
            + "running program is working in.",
    };

    /// <summary>
    /// The folders this provider would reach into, and any that named themselves and were refused.
    /// Resolved once per planning pass, because both the presence probe and the plan ask for them.
    /// </summary>
    private TempRootSet Roots => _roots ??= TempRoots.Resolve(Environment, _system);

    public override void InvalidateCaches()
    {
        _liveTrees.Invalidate();
        _roots = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// Present where one of the folders is actually there, which on an ordinary machine is always.
    ///
    /// A row that is always present is correct here, and the opposite of a toolchain probe: there is
    /// no "temporary files are not installed" state to report, and a machine with an empty scratch
    /// folder should read as already clear rather than as missing.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(DeclaredPaths().Any(LongPath.DirectoryExists));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        // Fixed once, here, for the same reason MinimumAge is an instant rather than a duration: the
        // preview and the clean must agree about which files are old enough, however long the
        // preview sits on screen before the user presses Clean.
        var floor = MinimumAge.Within(StaleAfter, DateTime.UtcNow);
        var effective = MinimumAge.Stricter(keep, floor);

        var scan = DeclaredLocations.Examine(Roots.Roots, ct);
        var notes = new List<PlanNote>(scan.Notes);

        if (TempRoots.NoteFor(Roots.Refused) is { } refusal)
        {
            notes.Add(refusal);
        }

        if (scan.FoundNothing)
        {
            return EmptyPlan("This machine has no temporary folder Deguffer can reach.") with
            {
                Notes = notes,
            };
        }

        var live = _liveTrees.FindLiveChildren([.. scan.Targets.Select(t => t.Path)], ct);
        var (planned, measured) = await PlanDeletionsAsync(scan.Targets, effective, ct).ConfigureAwait(false);
        var (steps, spared) = await SpareAsync(planned, live, effective, ct).ConfigureAwait(false);

        notes.Add(new PlanNote(
            PlanNoteSeverity.Information,
            $"Only what nothing has touched for {floor.Describe()} is offered. A temporary "
            + "folder holds live working files among abandoned ones, and there is nothing but age "
            + "to tell them apart."));

        if (LiveNote(live.Live) is { } busy)
        {
            notes.Add(busy);
        }

        if (LiveTreeVeto.IncompleteNote(live.Complete) is { } incomplete)
        {
            notes.Add(incomplete);
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
            ProtectedPaths = Protect([.. scan.Protected, .. spared]),
            Notes = notes,

            // §5.3's floor travels on the plan, so the removal applies exactly the cut-off the
            // preview measured against. CleanupProviderBase.PlanAsync will not loosen it.
            Keep = effective,
            Fallback = measured.Fallback,
            WasNotExamined = scan.NothingWasExamined,
        };
    }

    /// <summary>
    /// Hand each folder's step the entries it must leave alone, and take their bytes back out of
    /// its estimate.
    ///
    /// <para>The subtraction is the half that is easy to leave out and would not look wrong. A step
    /// that named its spared entries but still counted them would promise bytes the clean has
    /// already undertaken not to take — §5.4's "prune, see no change, lose trust" arriving from the
    /// other direction, and arriving on precisely the folders a busy machine has most of.</para>
    ///
    /// <para>Both figures are measured under the same guard, which is what makes the subtraction
    /// between two of the same quantity rather than between a guarded figure and an unguarded one.
    /// See <see cref="CleanupProviderBase.MeasureSparedAsync"/>.</para>
    /// </summary>
    private async Task<(IReadOnlyList<CleanupStep> Steps, IReadOnlyList<(string Path, string Reason)> Spared)>
        SpareAsync(
            IReadOnlyList<CleanupStep> planned,
            LiveTreeFindings live,
            MinimumAge keep,
            CancellationToken ct)
    {
        var steps = new List<CleanupStep>(planned.Count);
        var spared = new List<(string Path, string Reason)>();

        foreach (var step in planned)
        {
            ct.ThrowIfCancellationRequested();

            if (step is not ClearDirectoryStep clear)
            {
                steps.Add(step);
                continue;
            }

            var held = live.Live.Where(l => LongPath.Contains(clear.Path, l.Directory)).ToList();

            if (held.Count == 0)
            {
                steps.Add(clear);
                continue;
            }

            var paths = held.Select(h => h.Directory).ToList();
            var measured = await MeasureSparedAsync(paths, keep, ct).ConfigureAwait(false);

            steps.Add(clear with
            {
                Spared = paths,
                Estimated = clear.Estimated - measured.Total,
            });

            spared.AddRange(held.Select(h =>
                (h.Directory, $"Left alone because {string.Join("; ", h.Holders)}.")));
        }

        return (steps, spared);
    }

    /// <summary>
    /// What the user is told about the entries that were held back, or null where none were.
    ///
    /// <para>A warning rather than information, on <see cref="LiveTreeVeto.NoteFor"/>'s reasoning:
    /// the plan is smaller than the folder suggests, and the reason is something the user can act on
    /// by closing a program. It is written here rather than reusing that method because that one
    /// names each directory's project folder, which a scratch entry does not have.</para>
    /// </summary>
    private static PlanNote? LiveNote(IReadOnlyList<LiveTree> live)
    {
        if (live.Count == 0)
        {
            return null;
        }

        var held = live.Select(l =>
            $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(l.Directory))} "
            + $"({string.Join("; ", l.Holders)})");

        return new PlanNote(
            PlanNoteSeverity.Warning,
            $"Left {string.Join(", ", held)} alone. " + (live.Count == 1
                ? "Close what is using it and preview again to include it."
                : "Close what is using each one and preview again to include them."));
    }

    private IEnumerable<string> DeclaredPaths() =>
        from root in Roots.Roots
        from location in root.Locations
        select Path.Combine(root.Path, location.RelativePath);
}
