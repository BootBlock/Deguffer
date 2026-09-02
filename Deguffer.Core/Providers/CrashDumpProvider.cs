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
/// <para><b>Most of this needs administrator rights, and the plan says so rather than finding out
/// during execution.</b> Only <c>%LOCALAPPDATA%\CrashDumps</c> is removable by the signed-in user.
/// A step under <c>C:\Windows</c> or <c>%PROGRAMDATA%</c> carries
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
/// filter would take it away and would also have to change the grain from one directory to one
/// dump, which §7's age column is not asking for.</para>
/// </summary>
public sealed class CrashDumpProvider : CleanupProviderBase
{
    private readonly IReadOnlyList<DeclaredRoot> _roots;

    public CrashDumpProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _roots = Declare(system ?? SystemDirectories.Current);
    }

    public override string Id => "crash-dumps";

    public override string Name => "Crash dumps and error reports";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override string WhatHappensOnNextUse =>
        "The record of every crash and stop error listed here is destroyed, so none of it can be "
        + "attached to a bug report or opened in a debugger afterwards. Windows keeps writing new "
        + "dumps exactly as before, and nothing that is running is affected.";

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
    /// Presence is a declared path actually being there. Every one of these roots exists on every
    /// Windows machine, so reading a root as a hit would report this source everywhere and then plan
    /// nothing on a machine that has never crashed.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(DeclaredPaths().Any(p => p.IsFile ? LongPath.FileExists(p.Path) : LongPath.DirectoryExists(p.Path)));

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        var scan = DeclaredLocations.Examine(_roots, ct);

        if (scan.FoundNothing)
        {
            return EmptyPlan("Windows has written no crash dumps or error reports on this machine.");
        }

        var notes = new List<PlanNote>(scan.Notes);

        var (steps, measured) = await PlanDeletionsAsync(scan.Targets, ct).ConfigureAwait(false);

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
            ProtectedPaths = Protect([.. scan.Protected]),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    /// <summary>
    /// The five locations, and the two §9 exclusions that must be shown to survive a rule reaching
    /// into the same directory.
    ///
    /// <c>%PROGRAMDATA%</c> is declared as the root rather than the <c>WER</c> folder inside it, so
    /// that <c>Package Cache</c> — §9's installer package cache, and 6.7 GB on the audited machine —
    /// is a named survivor of this provider rather than merely something it never mentions.
    /// </summary>
    private IReadOnlyList<DeclaredRoot> Declare(ISystemDirectories system) =>
    [
        new DeclaredRoot(
            Environment.LocalAppData,
            "The profile's local application data must survive — only the crash dump folder inside "
            + "it is removed.",
            RequiresElevation: false,
            [
                new DeclaredLocation(
                    "CrashDumps",
                    "Dumps written when an application crashed. Each one is the record of a single "
                    + "failure and nothing re-creates it."),
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
