using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The dumps and error reports Windows wrote when something failed (0.15 GB on the audited machine,
/// and the size of installed memory after a single stop error on a machine set to write a complete
/// dump).
///
/// <para><b>Tier 3, and the survey that proposed these called them Tier 1.</b> That was wrong, and
/// it is worth saying why rather than quietly correcting it. §3's Tier 1 requires that whatever
/// produced the content re-creates it on demand, so that nothing is lost. Nothing re-creates a crash
/// dump: it is the record of an event, and the event will not happen again to order. The same
/// sentence in §3 that defines Tier 3 lists what these are — logs and records — and the consequence
/// column says the loss is permanent, which is exactly right here. A user halfway through a bug
/// report has the only copy of the evidence in this folder.</para>
///
/// <para><b>§5.2 against the operating system's own directory.</b> <c>C:\Windows</c> is the most
/// dangerous parent Deguffer has ever reached into, so it is never enumerated and never a target —
/// only the paths named in <see cref="Roots"/> are, and they are absolute rather than discovered.
/// That is stricter than the recognised-child rule every other provider uses, because there is no
/// enumeration through which an unnamed sibling could be reached at all. §9's exclusions are named
/// as survivors on top of that, so a run produces evidence that a rule reaching in here did not
/// reach <c>WinSxS</c> or <c>Windows\Installer</c>.</para>
///
/// <para><b>A place this machine's settings move dumps to is the one thing listed.</b> It can be
/// any folder, so it is never a target: only the dumps directly inside it are, each by name and
/// only where its header is a dump's. See <see cref="RelocatedDumps"/>.</para>
///
/// <para><b>Most of this needs administrator rights, and the plan says so rather than finding out
/// during execution.</b> Only the folders under <c>%LOCALAPPDATA%</c>, and application dumps a
/// setting moves elsewhere, are removable by the signed-in user. A step under <c>C:\Windows</c> or
/// <c>%PROGRAMDATA%</c>, or a kernel dump a setting moves elsewhere, carries
/// <see cref="CleanupStep.RequiresElevation"/>, which is a different claim from
/// <see cref="FallbackReason.NotElevated"/>: that one says a size was walked for rather than read,
/// and this one says the removal cannot happen. Silently failing at execution time and quietly
/// omitting the location are both worse than showing it and naming what it needs.</para>
///
/// <para><b>§5.1 is answered rather than skipped, and the answer is "no".</b> Windows does ship a
/// route: Disk Cleanup registers <c>VolumeCaches</c> handlers for the memory dump, the minidumps
/// and the error reports, and <c>cleanmgr /sagerun</c> drives them. It is not used for the reason
/// the Recycle Bin reached first — the preview outranks it. A handler cannot be selected without
/// first writing <c>StateFlags</c> into the machine's own registry, which is a change to the user's
/// Disk Cleanup configuration made on their behalf, and the run then reports nothing back. This
/// plan names each location with a size and a date, and §5.6 asserts what survived beside it, none
/// of which a call that returns a number for the volume could support. The second reason is §5.2:
/// the whole safety property here is which paths under <c>C:\Windows</c> are reached, and handing
/// that decision to a shell component puts it outside the code the rule is checkable in.</para>
///
/// <para><b>No age filter, deliberately.</b> A dump written this morning may be the only evidence in
/// a bug report somebody is still writing, which is a real hazard — but the answer to it is the tier
/// and the age column, not a cut-off. §5.3's exclusion for <c>%TEMP%</c> exists because live working
/// files sit among dead ones and look identical; here nothing is live except a dump still being
/// written, and that one is held open and skipped. So each step carries the newest write inside it,
/// Tier 3 keeps it unselected and confirmed before it runs, and the decision stays theirs. A
/// filter would take it away, and §7's age column is not asking for one.</para>
/// </summary>
public sealed class CrashDumpProvider : CleanupProviderBase
{
    private readonly IReadOnlyList<DeclaredRoot> _roots;
    private readonly ISystemDirectories _system;
    private readonly PassMemo<RelocatedDumpScan> _relocated = new();

    public CrashDumpProvider(
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
        _roots = Declare(_system);
    }

    public override string Id => "crash-dumps";

    public override string Name => "Crash dumps and error reports";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The record of every crash and stop error listed here is destroyed, so none of it can be "
        + "attached to a bug report or opened in a debugger afterwards. Windows keeps writing new "
        + "dumps exactly as before, and nothing that is running is affected.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.Windows,
        Application = "Windows Error Reporting, and the kernel's own crash dump writer",
        Publisher = "Microsoft",
        Purpose = "When an application or Windows itself fails, what memory held at that moment "
            + "is written here, along with the report queued for sending. A single stop error on a "
            + "machine set to write a complete dump costs as much disk as the machine has memory.",
        Recommendation = "Nothing re-creates a crash dump: it is the record of an event that will "
            + "not happen again to order, and somebody halfway through a bug report has the only "
            + "copy of the evidence here.",
    };

    /// <summary>
    /// What this provider names, root by root. Exposed so tests can assert that no root is ever a
    /// target and that the §9 exclusions are asserted rather than merely omitted.
    /// </summary>
    public IReadOnlyList<DeclaredRoot> Roots => _roots;

    /// <summary>
    /// §5.3: <c>WerFault</c> is what writes these, so a dump may be arriving while the plan is being
    /// read. Anything it holds open is left in place, which is the correct outcome rather than a
    /// failure — but the user should know before confirming that the folder is in use.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["WerFault", "WerFaultSecure"];

    /// <summary>
    /// Presence is a declared path actually being there, or a dump in a place this machine's settings
    /// move dumps to. Every one of these roots exists on every Windows machine, so reading a root as
    /// a hit would report this source everywhere and then plan nothing on a machine that has never
    /// crashed.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(
            DeclaredPaths().Any(p => p.IsFile ? LongPath.FileMayExist(p.Path) : LongPath.DirectoryMayExist(p.Path))
            || ExamineRelocated(ct).FoundAnything);

    /// <summary>
    /// Each declared dump folder, and the kernel's full dump file, whole: everything in them is a
    /// record this row offers, and the folders holding them are not this row's. In a place a setting
    /// moves dumps to, only each dump is this row's.
    /// </summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            .. DeclaredPaths().Select(declared => CleanedPlace.Whole(declared.Path)),
            .. ExamineRelocated(ct).Targets.Select(dump => CleanedPlace.Whole(dump.Path)),
        ]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var scan = DeclaredLocations.Examine(_roots, ct);
        var relocated = ExamineRelocated(ct);

        if (scan.FoundNothing && relocated.Targets.Count == 0)
        {
            return NothingFound(relocated);
        }

        var notes = new List<PlanNote>(scan.Notes);
        notes.AddRange(relocated.Notes);

        if (!scan.Targets.Any(t => t.RequiresElevation) && relocated.Targets.Any(t => t.RequiresElevation))
        {
            notes.Add(DeclaredLocations.ElevationNote);
        }

        IReadOnlyList<DeletionTarget> targets = [.. scan.Targets, .. relocated.Targets];

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

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
            Steps = steps,
            ProtectedPaths = Protect([.. scan.Protected, .. relocated.Protected]),
            Notes = notes,
            Fallback = measured.Fallback,
            WasNotExamined = targets.Count == 0 && (scan.Declined.Count > 0 || relocated.LeftUnexamined),
            HasUnreadableRoot = scan.CouldNotBeReached || relocated.Unreachable.Count > 0,
        };
    }

    /// <summary>
    /// The plan when no declared location is there and no moved place holds a dump to offer.
    ///
    /// <para>"Already clear" is a claim about every place Windows writes these, so it is made only
    /// where every one was looked at. A moved place Windows would not describe, or one Deguffer
    /// would not look into, leaves the row saying so instead.</para>
    /// </summary>
    private CleanupPlan NothingFound(RelocatedDumpScan relocated)
    {
        var plan = relocated.Unreachable.Count > 0
            ? UnreadableRootPlan(relocated.Unreachable)
            : relocated.LeftUnexamined
                ? UnexaminedPlan(
                    "None of the places Deguffer could look in holds a crash dump or error report, and "
                    + "it could not look in all of them.")
                : EmptyPlan(
                    "None of the places Windows is set to write crash dumps and error reports to holds any.");

        // An unreachable place's own warning is already in the plan, so only the other sentences are
        // added: each moved place is named whatever was found there.
        return plan with
        {
            Notes = [.. plan.Notes, .. relocated.Notes.Where(note => !plan.Notes.Contains(note))],
        };
    }

    /// <summary>
    /// The places this machine's settings move dumps to, examined once per planning pass (G4).
    /// Presence, planning and Explore's refusals all ask the same question of the same settings, and
    /// each look opens every dump it meets to read its header.
    /// </summary>
    private RelocatedDumpScan ExamineRelocated(CancellationToken ct) => _relocated.Get(() =>
        RelocatedDumps.Examine(
            CrashDumpSettings.Read(Environment),
            [.. DeclaredPaths().Select(declared => declared.Path)],
            Environment,
            _system,
            Volumes,
            ct));

    public override void InvalidateCaches()
    {
        _relocated.Forget();
        Volumes.Invalidate();
        base.InvalidateCaches();
    }

    /// <summary>
    /// The eight locations, and the §9 exclusions that must be shown to survive a rule reaching into
    /// the same directory.
    ///
    /// <c>%PROGRAMDATA%</c> is declared as the root rather than the <c>WER</c> folder inside it, so
    /// that the two installer caches §9 excludes are named survivors of this provider rather than
    /// merely things it never mentions. They are separate directories holding the same kind of
    /// thing: <c>Package Cache</c> was 6.7 GB in the founding audit, and Visual Studio's own payload
    /// store 7.7 GB when it was measured.
    /// </summary>
    private IReadOnlyList<DeclaredRoot> Declare(ISystemDirectories system) =>
    [
        new DeclaredRoot(
            Environment.LocalAppData,
            "The profile's local application data must survive — only the crash dump and error "
            + "report folders inside it are removed.",
            RequiresElevation: false,
            [
                new DeclaredLocation(
                    "CrashDumps",
                    "Dumps written when an application crashed. Each one is the record of a single "
                    + "failure and nothing re-creates it."),
                new DeclaredLocation(
                    Path.Combine("Microsoft", "Windows", "WER", "ReportArchive"),
                    "Error reports Windows has already sent for this account's applications. The "
                    + "archive is this machine's own copy and is not fetched back."),
                new DeclaredLocation(
                    Path.Combine("Microsoft", "Windows", "WER", "ReportQueue"),
                    "Error reports gathered for this account's applications but not yet sent. "
                    + "Removing one means it is never sent."),
            ],
            []),

        new DeclaredRoot(
            system.ProgramData,
            "The machine-wide application data directory must survive — only the error report "
            + "folders named inside it are removed.",
            RequiresElevation: true,
            [
                new DeclaredLocation(
                    Path.Combine("Microsoft", "Windows", "WER", "ReportArchive"),
                    "Error reports Windows has already sent. The archive is this machine's own copy "
                    + "and is not fetched back."),
                new DeclaredLocation(
                    Path.Combine("Microsoft", "Windows", "WER", "ReportQueue"),
                    "Error reports gathered but not yet sent. Removing one means it is never sent."),
            ],
            [
                ("Package Cache",
                    "The installer package cache. §9 keeps Windows and installer component caches "
                    + "out of Deguffer entirely, because removing one breaks repair and uninstall."),
                (Path.Combine("Microsoft", "VisualStudio", "Packages"),
                    "The Visual Studio installer's payload cache. §9 excludes it for the same reason "
                    + "as the one above: removing it breaks an offline repair or modify, and the "
                    + "installer's own records of each installed product sit among the payloads."),
            ]),

        WindowsSystemRoot.Holding(
            system,
            new DeclaredLocation(
                "LiveKernelReports",
                "Kernel dumps taken when a driver was reset without stopping the machine."),
            new DeclaredLocation(
                "Minidump",
                "Small kernel dumps, one for each stop error this machine has had."),
            new DeclaredLocation(
                "MEMORY.DMP",
                "The full kernel dump from the last stop error. On a machine set to write a "
                + "complete dump this is the size of installed memory.",
                DeclaredLocationKind.File)),
    ];

    /// <summary>
    /// Every path this provider could ever target, by declaration rather than by enumeration — so
    /// answering "is there anything here?" costs one existence check each and can never reach
    /// anything the table does not name.
    /// </summary>
    private IEnumerable<(string Path, bool IsFile)> DeclaredPaths() =>
        from root in _roots
        from location in root.Locations
        select (Path.Combine(root.Path, location.RelativePath), location.Kind == DeclaredLocationKind.File);
}
