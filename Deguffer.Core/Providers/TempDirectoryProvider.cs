using Deguffer.Core.Configuration;
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
/// <item><b>An age filter of this provider's own, which the guard on recently changed files cannot
/// loosen.</b> The plan carries it as its own <see cref="CleanupPlan.Keep"/>, and
/// <see cref="MinimumAge.Stricter"/> is what makes the two compose rather than compete. Seven days
/// by default, which is what Windows' own Disk Cleanup applies to these two folders — so an
/// untouched install offers what the machine already behaves as though it would.
///
/// <para>It is a setting, and
/// <see cref="Configuration.AppPreferences.MinimumTemporaryFileAgeDays"/> may be zero, which is no
/// age limit at all. That is the one value on this page that removes a safety rule rather than
/// adjusting it, so the row says so on a warning whenever it is in force. The other two
/// requirements below are unaffected by it, and they are what is left.</para></item>
/// <item><b>Exclusion of what a running program is using.</b> An entry a process is running from or
/// working in is never a target, however old its files are: the process holding it may have opened
/// nothing this minute, so nothing is locked and the timestamps prove nothing.
/// <see cref="ILiveTreeInspector.FindLiveChildren"/> answers that in one pass over the process
/// table, and each entry it names is asserted to have survived (§5.6).</item>
/// <item><b>A refusal treated as ordinary.</b> A file Windows will not release is live state, or
/// something guarding it, so <see cref="DirectoryRemover"/> leaves it and moves on. What it leaves is
/// reported with its size and its reason, and the next preview leaves out whatever is still refused,
/// apart from a running program's own files — see <see cref="RefusalRecord"/>. This folder is where that was found: every file in browser
/// profiles that test runners had left here was refused below the ACL, most likely by security
/// software, so each clean reported a quarter of a million files "in use" and each preview offered
/// their 5.9 GB again.</item>
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
    /// The bounds on <see cref="Configuration.AppPreferences.MinimumTemporaryFileAgeDays"/>,
    /// clamped here as well as in the settings box because nothing validates
    /// <c>preferences.json</c> on the way in.
    ///
    /// <para><b>Zero is deliberately the floor, and it means no age limit at all.</b> Every other
    /// bound in this project exists to stop a value reaching something dangerous;
    /// <see cref="FileHistoryProvider.MinimumRetentionDays"/> is one for that reason. This one
    /// admits the dangerous value on purpose, because the alternative was a rule nobody could reach
    /// past on their own machine — and it is admitted with the consequence stated on the row rather
    /// than quietly. See the preference for what still protects a file at zero and what does
    /// not.</para>
    /// </summary>
    public const int MinimumStaleDays = 0;

    /// <summary>
    /// A year. Past that the setting stops being "leave what is in use alone" and becomes a way of
    /// switching the row off, which the tick box beside it already does more plainly.
    /// </summary>
    public const int MaximumStaleDays = 365;

    private readonly ILiveTreeInspector _liveTrees;
    private readonly ISystemDirectories _system;
    private readonly ICurrentPreferences _preferences;
    private TempRootSet? _roots;

    public TempDirectoryProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        ILiveTreeInspector? liveTrees = null,
        ICurrentPreferences? preferences = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _system = system ?? SystemDirectories.Current;
        _liveTrees = liveTrees ?? LiveTreeInspector.Default;
        _preferences = preferences ?? DefaultPreferences.Instance;
    }

    public override string Id => "temp-directories";

    public override string Name => "Temporary files";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    /// <summary>
    /// §7's cost sentence, and it quotes the cut-off actually in force rather than a number this
    /// provider used to hard-code.
    ///
    /// <para><b>It has to move with the setting, and it is the sentence most likely to be
    /// believed.</b> A row asserting "nothing offered here was touched inside seven days" while
    /// running at two days is false by five, about the primary — and at zero the only — safety
    /// mechanism the location has. The plan note beside it already quoted the real number, so the
    /// two sentences on one row disagreed with each other.</para>
    /// </summary>
    public override string WhatHappensOnNextUse =>
        $"Nothing that is running is affected. {OfferedPhrase(ConfiguredDays)} Anything a running "
        + "program is working in is left where it is, and what you lose is whatever a program "
        + "stored in a temporary folder and still expects to find there — which installers and "
        + "crash reporters occasionally do.";

    /// <summary>
    /// Computed rather than fixed at construction, for the reason
    /// <see cref="WhatHappensOnNextUse"/> gives: the recommendation states the same cut-off, and a
    /// provider whose two descriptions disagree about it is worse than one that states neither.
    /// </summary>
    public override ProviderDescription Description => new()
    {
        Application = "Windows, and every program on the machine that writes scratch files",
        Publisher = "Microsoft, and whichever program left each file behind",
        Purpose = "Installers, compilers, browsers and test runners unpack and scratch in a "
            + "temporary folder and are meant to clear up afterwards. A great many never do, so "
            + "both folders grow without limit — this account's own, and the one Windows and its "
            + "services share.",
        Recommendation = ConfiguredDays > 0
            ? "Live working files sit among abandoned ones and look identical, so Deguffer offers "
                + $"only what nothing has touched for {Describe(ConfiguredDays)} and leaves alone "
                + "anything a running program is working in."
            : "Live working files sit among abandoned ones and look identical, and the age limit "
                + "that told them apart is set to none. What a running program is working in, or "
                + "holds open, is still left alone; nothing else here is, unless your guard on "
                + "recently changed files holds it.",
    };

    /// <summary>
    /// The age limit in force, in whole days, clamped because nothing validates
    /// <c>preferences.json</c> on the way in.
    ///
    /// <para>Read at the moment it is needed rather than held from construction, so a change in
    /// Settings takes effect from the next preview — and read through one property rather than at
    /// each of the three places that quote it, because a row whose sentences disagreed about the
    /// cut-off is the defect this replaced.</para>
    /// </summary>
    private int ConfiguredDays => Math.Clamp(
        _preferences.Current.MinimumTemporaryFileAgeDays, MinimumStaleDays, MaximumStaleDays);

    /// <summary>
    /// The sentence naming what is offered, for the two places that both state it.
    ///
    /// <para><b>The zero case says what this location does rather than what the run will do</b>, and
    /// the distinction is the difference between true and false. This property is the provider's,
    /// asked without a plan, so it cannot see the user's own guard on recently changed files — and
    /// on a machine with no age limit here but an eight-hour guard set there, "everything is offered
    /// however recently it was written" is contradicted by the estimate, by the plan's own note, and
    /// by the note <see cref="CleanupProviderBase"/> adds beside it. Naming the absence of a limit
    /// <em>of its own</em> is true either way, and points at the setting that is doing the
    /// work.</para>
    /// </summary>
    private static string OfferedPhrase(int days) => days > 0
        ? $"Everything offered here was last touched more than {Describe(days)} ago."
        : "This location has no age limit of its own, so nothing here is held back for being "
            + "recent unless your guard on recently changed files holds it.";

    /// <summary>
    /// A whole number of days as the phrase a row prints. <see cref="MinimumAge.Describe"/> answers
    /// for a window that is on; this one is asked before there is a window, and about a value that
    /// may be zero.
    /// </summary>
    private static string Describe(int days) => days == 1 ? "a day" : $"{days} days";

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
        var days = ConfiguredDays;

        // Fixed once, here, for the same reason MinimumAge is an instant rather than a duration: the
        // preview and the clean must agree about which files are old enough, however long the
        // preview sits on screen before the user presses Clean.
        //
        // A window of zero is MinimumAge.Off, which is the whole of what "no age limit" needs to
        // mean — Stricter then yields the user's own guard, or nothing at all.
        var floor = MinimumAge.Within(TimeSpan.FromDays(days), DateTime.UtcNow);
        var effective = MinimumAge.Stricter(keep, floor);

        var scan = DeclaredLocations.Examine(Roots.Roots, ct);
        var notes = new List<PlanNote>(scan.Notes);

        if (TempRoots.NoteFor(Roots.Refused) is { } refusal)
        {
            notes.Add(refusal);
        }

        if (scan.FoundNothing)
        {
            var empty = EmptyPlan("This machine has no temporary folder Deguffer can reach.");

            // Appended rather than assigned. Replacing the list drops the sentence EmptyPlan just
            // wrote, which is the only thing on the row explaining the zero beside it.
            return empty with { Notes = [.. empty.Notes, .. notes] };
        }

        var live = _liveTrees.FindLiveChildren([.. scan.Targets.Select(t => t.Path)], ct);
        var (planned, measured) = await PlanDeletionsAsync(scan.Targets, effective, ct).ConfigureAwait(false);
        var (steps, spared) = await SpareAsync(planned, live, effective, ct).ConfigureAwait(false);

        // Three sentences, because there are three states and the difference between them is a rule
        // and its absence. Saying "only what nothing has touched for 0 days is offered" would read
        // like a safeguard and describe none.
        //
        // The alarming one is chosen on the guard actually in force rather than on this provider's
        // own, which is the distinction that made it wrong: with no floor but a guard the user set,
        // recent files are held back after all, and a warning saying otherwise contradicts the note
        // CleanupProviderBase adds a moment later.
        notes.Add((floor.IsOn, effective.IsOn) switch
        {
            (true, _) => new PlanNote(
                PlanNoteSeverity.Information,
                $"Only what nothing has touched for {floor.Describe()} is offered. A temporary "
                + "folder holds live working files among abandoned ones, and there is nothing but "
                + "age to tell them apart."),

            // No limit of its own, and the user's guard is the whole of what holds anything back.
            // Information rather than a warning, because something does.
            (false, true) => new PlanNote(
                PlanNoteSeverity.Information,
                "No age limit is set for temporary files, so the only thing holding anything back "
                + "here is your guard on recently changed files."),

            _ => new PlanNote(
                PlanNoteSeverity.Warning,
                "No age limit is set, so everything in these folders is offered however recently it "
                + "was written. A temporary folder holds live working files among abandoned ones, "
                + "and age is the only thing that tells them apart. Anything a running program is "
                + "working in, or holds open, is still left alone."),
        });

        // Named by the entry alone. LiveTreeVeto's default names a directory by the project folder
        // holding it, which a scratch entry does not have.
        if (LiveTreeVeto.NoteFor(live.Live, l => Path.GetFileName(Path.TrimEndingDirectorySeparator(l.Directory)))
            is { } busy)
        {
            notes.Add(busy);
        }

        if (LiveTreeVeto.IncompleteNote(
            live.Complete, "Close whatever is working in them before cleaning.") is { } incomplete)
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

    private IEnumerable<string> DeclaredPaths() =>
        from root in Roots.Roots
        from location in root.Locations
        select Path.Combine(root.Path, location.RelativePath);
}
