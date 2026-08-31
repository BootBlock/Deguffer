using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The logs Windows writes while servicing itself (64 MB on the audited machine, and routinely
/// gigabytes on one with a long update history).
///
/// <para><b>Tier 3, and the survey that proposed these called them Tier 1.</b> §3's Tier 1 requires
/// that whatever produced the content re-creates it, so that nothing is lost. What is re-created
/// here is the next log, never the ones removed: a servicing operation writes a record of itself,
/// and that operation does not run again to order. §3 names logs in its Tier 3 row, and the property
/// that puts them there — the loss is permanent — holds for these as much as for a chat history.
/// The case where it bites is narrow and real: somebody diagnosing a failed update or reading what
/// <c>sfc</c> just wrote is looking at exactly these files. Tier 3 keeps the row unselected and the
/// age column says how recently something wrote to it, which together is the answer.</para>
///
/// <para>Separate from <see cref="CrashDumpProvider"/> rather than one provider over both, because
/// "crash dumps and servicing logs" needs the word "and" to describe it, which is G1's own test for
/// two types. They are two subjects with two consequences, and keeping them apart lets somebody
/// clear the update logs without emptying the evidence of a crash.</para>
///
/// <para><b>§5.2 against the operating system's own directory.</b> <c>C:\Windows</c> is never
/// enumerated and never a target. Only the paths declared below are, and they are absolute rather
/// than discovered, so there is no enumeration through which an unnamed sibling could be reached.
/// <see cref="WindowsSystemRoot"/> carries §9's exclusions onto the declaration, so a run produces
/// evidence that a rule reaching in here did not reach <c>WinSxS</c> or <c>Windows\Installer</c>.
/// Two of the four are nested — <c>Logs\CBS</c> and <c>System32\LogFiles\WMI\RtBackup</c> — and
/// every directory passed through on the way down is checked for being a link and asserted to have
/// survived.</para>
///
/// <para><b>Every one of these needs administrator rights</b>, so each step says so and the plan
/// carries the sentence. §5.3 is also unusually live: the WMI service holds its own trace files
/// open, and the servicing stack holds the log it is currently writing, so an access denial here is
/// the ordinary case rather than a fault. Whatever is held stays.</para>
/// </summary>
public sealed class WindowsServicingLogProvider : CleanupProviderBase
{
    private readonly IReadOnlyList<DeclaredRoot> _roots;

    public WindowsServicingLogProvider(
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

    public override string Id => "windows-servicing-logs";

    public override string Name => "Windows servicing logs";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override string WhatHappensOnNextUse =>
        "The record of every update, repair and upgrade this machine has already carried out is "
        + "destroyed, so none of it can be read afterwards to work out why one of them failed. "
        + "Windows writes a fresh log the next time it services itself, and updating still works "
        + "exactly as before.";

    /// <summary>
    /// What this provider names, root by root. Exposed so tests can assert that the Windows
    /// directory is never a target and that §9's exclusions are asserted rather than merely omitted.
    /// </summary>
    public IReadOnlyList<DeclaredRoot> Roots => _roots;

    /// <summary>
    /// §5.3: the servicing stack writes <c>CBS.log</c> from <c>TiWorker</c> under
    /// <c>TrustedInstaller</c>, so an update in flight means the log is open. The WMI service holds
    /// <c>RtBackup</c> open too, but it runs inside a shared <c>svchost</c> and so has no name worth
    /// giving the user — which is why the plan's own wording covers it instead.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["TiWorker", "TrustedInstaller"];

    /// <summary>
    /// Presence is a declared path actually being there. The Windows directory exists everywhere, so
    /// treating a root as a hit would report this source on every machine and then plan nothing.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(DeclaredPaths().Any(LongPath.DirectoryExists));

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        var scan = DeclaredLocations.Examine(_roots, ct);

        if (scan.FoundNothing)
        {
            return EmptyPlan("Windows is holding no servicing logs in the places Deguffer knows about.");
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

        // Named rather than left to be discovered from a step that reclaimed less than its size.
        // Unlike every other provider's §5.3 warning this one is not conditional on a process: the
        // service that holds these is always running, so a log left behind is the expected outcome.
        notes.Add(new PlanNote(
            PlanNoteSeverity.Information,
            "Windows keeps some of these files open while it is running, and anything held open is "
            + "left in place. Clearing less than the size shown is the normal result here."));

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
    /// The four locations. <c>Logs</c>, <c>System32</c>, <c>System32\LogFiles</c> and
    /// <c>System32\LogFiles\WMI</c> are containers rather than targets, and each is asserted to have
    /// survived — the same treatment Chromium's <c>Cache</c> needed, and for the same reason: the
    /// directory really is left standing while something inside it is removed, so the generic
    /// "we did not recognise that" wording would be false about it.
    /// </summary>
    private static IReadOnlyList<DeclaredRoot> Declare(ISystemDirectories system) =>
    [
        WindowsSystemRoot.Holding(
            system,
            new DeclaredLocation(
                Path.Combine("Logs", "CBS"),
                "Component servicing logs — the trail of what Windows added, removed or repaired."),
            new DeclaredLocation(
                Path.Combine("Logs", "WindowsUpdate"),
                "Windows Update trace files, kept from updates that have already been installed."),
            new DeclaredLocation(
                "Panther",
                "Setup logs from Windows installations and in-place upgrades that have finished."),
            new DeclaredLocation(
                Path.Combine("System32", "LogFiles", "WMI", "RtBackup"),
                "Backup trace files for the event sessions the WMI service runs. The service holds "
                + "the current ones open, and those are left in place.")),
    ];

    /// <summary>
    /// Every path this provider could ever target, by declaration rather than by enumeration — so
    /// answering "is there anything here?" costs one existence check each and can never reach
    /// anything the table does not name.
    /// </summary>
    private IEnumerable<string> DeclaredPaths() =>
        from root in _roots
        from location in root.Locations
        select Path.Combine(root.Path, location.RelativePath);
}
