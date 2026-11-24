using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// Hand one Disk Cleanup handler Windows registers the volume it clears, rather than deleting what it
/// clears (§5.1).
///
/// <para><b>Why the handler and never the path.</b> <c>Windows.old</c> is the case. Windows' own
/// <em>Previous Installations</em> registration asks its handler to remove the uninstall record as
/// well (<c>RemoveUninstall</c>), which is what Settings offers to go back from, and a tree removed by
/// path would leave that record naming a folder that is no longer there. The handler is Windows'
/// statement of what goes with the folder, and Deguffer does not know it.</para>
///
/// <para><b>Why it is a <see cref="DeleteStep"/>, for the reason <see cref="EmptyRecycleBinStep"/>
/// is one.</b> It destroys paths the plan named, sized and dated, so it belongs where §5.6's negative
/// and <see cref="CleanupPlan.TargetedPaths"/> look. What it shares with a <see cref="RunCommandStep"/>
/// is that Windows, not Deguffer, decides what goes. So the run's reach is unbounded while one is in
/// it (see <see cref="RunReach.Unbounded"/>), and it goes whole or not at all, which is why an Outlook
/// data file inside it withholds it and why a guard on recently changed files that would hold anything
/// back withdraws it.</para>
/// </summary>
/// <param name="Path">The directory the row is about, in display form.</param>
/// <param name="What">Why it is disposable, written for the user.</param>
public sealed record DiskCleanupStep(string Path, string What) : DeleteStep(Path, What)
{
    /// <summary>
    /// The handler's name, as Windows registers it under
    /// <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches</c>.
    /// </summary>
    public required string Handler { get; init; }

    /// <summary>
    /// The top of the volume the handler is asked to clear, in display form: the handler takes
    /// <c>C:\</c>, never <c>\\?\C:\</c>.
    /// </summary>
    public required string Volume { get; init; }

    /// <summary>
    /// The directories besides <see cref="DeleteStep.Path"/> that Windows registers the handler to
    /// clear, and that were there when the plan was made. Empty for a handler with one directory.
    /// </summary>
    public IReadOnlyList<string> AlsoClears { get; init; } = [];

    public override IReadOnlyList<string> Destroys => [Path, .. AlsoClears];

    /// <summary>The handler is chosen because Windows knows what goes with its directories, and Deguffer does not.</summary>
    public override bool ReachUnstated => true;

    public override string Description => $"{What} — {LongPath.Display(Path)}";
}
