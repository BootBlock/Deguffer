using System.Collections.Frozen;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// Invoke a tool's own eviction command (§5.1) — always preferred over deleting paths, because
/// the tool knows about locations we do not.
/// </summary>
public sealed record RunCommandStep(string FileName, string Arguments, string What) : CleanupStep
{
    /// <summary>
    /// The one item this command is sent to remove, where it names one, in display form. Null for a
    /// command that clears a whole cache, whose reach is the tool's to decide.
    ///
    /// <para>It makes the command a choice between items rather than a part of one location. A
    /// declined or kept item is then protected under §5.6 as a declined deletion is, which is sound
    /// here and nowhere else: the command that would have removed it never ran, and every other
    /// command in the plan names a different item.</para>
    /// </summary>
    public string? Removes { get; init; }

    /// <summary>
    /// How the tool marks <see cref="Removes"/> when it removes it later rather than at once. Null for
    /// a tool that removes its item before the command returns.
    /// </summary>
    public ScheduledRemoval? Scheduled { get; init; }

    /// <summary>
    /// Programs one of which must be running when the command runs. Empty for a command that needs
    /// none.
    ///
    /// <para><b>For a tool that starts its own application when it finds it closed.</b> <c>lms</c> is
    /// the case: it launches LM Studio as a service rather than fail. The plan was made while the
    /// program ran, and the user can close it while the preview is on screen, so the run asks again
    /// immediately before the command and does not run it if the program is gone.</para>
    /// </summary>
    public IReadOnlyList<string> RunsOnlyWhile { get; init; } = [];

    /// <summary>
    /// <see cref="Removes"/>, where the command names an item. <see cref="MeasuredPaths"/> never
    /// counts: it is a probe of where the tool was sent, and the tool decides what it takes there.
    /// </summary>
    public override IReadOnlyList<string> Subjects => Removes is { } item ? [item] : [];

    public override IReadOnlyList<string> Reaches => [.. MeasuredPaths, .. Subjects];

    /// <summary>
    /// Whether the tool decides what to remove from the settings of the account that runs it, kept
    /// somewhere other than where it removes: File History's own command reads the account's backup
    /// configuration from its profile and removes versions from a backup drive. See
    /// <see cref="CleanupStep.ActsForCallingAccount"/>.
    /// </summary>
    public bool ReadsCallingAccountSettings { get; init; }

    public override bool ActsForCallingAccount => ReadsCallingAccountSettings;

    /// <summary>
    /// What comes before the command, its checks, its look and its tool's measurement, only reads, and
    /// that measurement is what a reading kept from an earlier step may stand for, so the run marks the
    /// command alone as acting.
    /// </summary>
    public override bool ActsFromItsStart => false;

    /// <summary>
    /// The locations we expect the command to clear. The command remains the authority on *what*
    /// gets removed, which is the whole point of §5.1. NuGet's own clear reached two locations that
    /// were not under <c>.nuget</c> at all, so this list is a probe, never a target.
    ///
    /// <para>It has two readers, and a path added for one reaches the other. The executor measures
    /// these locations again to report what the command reclaimed. And §5.6 reads them as where the
    /// tool was sent: a protected folder holding one of them may end the run empty without an alarm
    /// (see <see cref="RunReach.ProbedPaths"/>). So a location listed only to improve the figure
    /// also excuses every protected folder above it.</para>
    /// </summary>
    public IReadOnlyList<string> MeasuredPaths { get; init; } = [];

    /// <summary>
    /// What <see cref="MeasuredPaths"/> held at plan time, where that is a different number from
    /// <see cref="CleanupStep.Estimated"/>. Null means they are the same figure, which is every
    /// provider whose estimate *is* its measurement of those paths.
    ///
    /// <para>Exists for the provider whose estimate is the tool's own accounting rather than
    /// Deguffer's: conda's dry run reports what its clean will free, while its package caches
    /// measure far larger, because everything an environment hard-links stays. Reporting the
    /// reclaim as "estimate minus what remains" would then compare two different kinds of number
    /// and call the result negative. The delta must subtract like from like, so the step carries
    /// Deguffer's own plan-time probe of the same paths the executor re-measures.</para>
    ///
    /// <para><b>It fixes the pairing, and the re-measurement is fixed elsewhere.</b> The "after"
    /// figure comes from the provider's own scanner, and <see cref="Scanning.DirectoryScanner"/>
    /// holds its volume index until something invalidates it — which happens once, at the start of a
    /// planning pass. Every command step therefore used to subtract two readings of one pre-command
    /// snapshot and report nothing reclaimed. That belonged to the executor rather than to any
    /// provider, and it is closed by
    /// <see cref="Scanning.IDirectoryScanner.MeasureFromDiskAsync"/>, which the executor's
    /// after-measure takes.</para>
    /// </summary>
    public ScanSize? MeasuredBefore { get; init; }

    /// <summary>
    /// Where the tool itself states what the command clears, the run asks it again immediately before
    /// and after the command rather than measuring <see cref="MeasuredPaths"/> from the disk. Null for every command whose
    /// figure is Deguffer's own measurement of those paths. See <see cref="IToolMeasurement"/>.
    ///
    /// <para>A step carrying one lists no <see cref="MeasuredPaths"/> unless it can name where the tool
    /// works, because those paths are also what §5.6 reads as where the tool was sent.</para>
    /// </summary>
    public IToolMeasurement? MeasuredBy { get; init; }

    /// <summary>
    /// Asked immediately before the command runs, where what its unchanged arguments reach can change
    /// between the preview and the clean: the tool names <see cref="Removes"/> by a handle it can give
    /// to something else, or decides what to remove from what it finds when it runs. Null for a
    /// command whose arguments reach only what the plan examined however the machine changes. See
    /// <see cref="ICommandTargetCheck"/>.
    /// </summary>
    public ICommandTargetCheck? TargetCheck { get; init; }

    /// <summary>
    /// Where the command asks Windows' servicing stack to do something, the record the run makes it
    /// in, so the stack it wakes is not taken for an update afterwards. Null for every command that
    /// does not reach the servicing stack. See <see cref="OwnServicingCalls"/>.
    /// </summary>
    public OwnServicingCalls? ServicingCalls { get; init; }

    public override OwnServicingCalls? ServicingRecord => ServicingCalls;

    /// <summary>
    /// Whether a clean cancelled while the command runs ends the tool, or lets it finish. Required,
    /// because only the provider knows what the tool leaves behind when it is stopped part-way: npm's
    /// hash-checked cache is only smaller, while a NuGet package folder or Windows' component store can
    /// be left damaged.
    /// See <see cref="CancelledCommand"/>.
    /// </summary>
    public required CancelledCommand WhenCancelled { get; init; }

    /// <summary>
    /// How the tool encodes what it writes, which is how the run reads its output into the step's
    /// outcome. See <see cref="ToolRun.Output"/>.
    /// </summary>
    public ToolOutputEncoding Output { get; init; }

    /// <summary>
    /// Variables the command starts without, for a tool that reads an inherited one as which files to
    /// act on (<see cref="ToolRun.Without"/>). Empty for every tool that reads its environment as given.
    /// </summary>
    public IReadOnlySet<string> Unset { get; init; } = FrozenSet<string>.Empty;

    public override string Description => $"{What} ({Path.GetFileName(FileName)} {Arguments})";

    /// <summary>
    /// The command itself, without where the tool happens to be installed. A tool that moves — an
    /// upgrade that lands under a new version directory, a PATH entry that resolves elsewhere — is
    /// running the same command on the same cache, and the user's choice about it still applies.
    /// </summary>
    public override string SelectionKey => $"{Path.GetFileName(FileName)} {Arguments}";
}
