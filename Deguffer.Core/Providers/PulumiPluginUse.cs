using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether anything is using one plugin in Pulumi's plugin cache now, asked again immediately before
/// the plugin is removed, by Pulumi's command or by path.
///
/// <para><b>Two signals.</b> Pulumi starts a plugin from its folder in the cache, as
/// <c>pulumi-resource-aws.exe</c> and the like, so a program running from or working in the folder is
/// using it. And while <c>pulumi</c> itself runs, a deployment may be about to start a plugin it has
/// already chosen, or be installing one, so nothing in the cache is removed until it ends. The plan
/// asked both, and a plugin offered on that answer can be started while the preview is on screen.</para>
/// </summary>
/// <param name="Processes">What reads the process table for Pulumi itself.</param>
/// <param name="LiveTrees">What reads where each program runs and works.</param>
/// <param name="Plugin">The plugin, whose folder is in display form.</param>
internal sealed record PulumiPluginUse(IProcessInspector Processes, ILiveTreeInspector LiveTrees, PulumiPlugin Plugin) : IUseCheck
{
    /// <summary>The programs whose running holds back the whole cache.</summary>
    public static readonly IReadOnlyList<string> PulumiProcessNames = ["pulumi"];

    /// <summary>Why the cache survives while Pulumi runs.</summary>
    public const string PulumiRunningReason =
        "Pulumi is running, and may be starting or installing a plugin, so nothing in its plugin cache is removed until it ends.";

    /// <summary>Why one plugin survives while a program runs from it.</summary>
    public const string InUseReason = "A program is running from this plugin, so it is left alone.";

    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct) =>
        WhyInUse(ct) is { } reason ? [new InUseNow(step.Path, reason)] : [];

    /// <summary>
    /// What is using the plugin now, as a lower-case clause with no full stop, or null where nothing is.
    /// A question that could not be asked in full is not a clear answer.
    /// </summary>
    public string? WhyInUse(CancellationToken ct)
    {
        // Both inspectors keep one reading for a planning pass, and the preview's may be past the limit.
        Processes.DiscardOlderThan(IUseCheck.ReadingLimit);
        LiveTrees.DiscardOlderThan(IUseCheck.ReadingLimit);

        if (Processes.FindRunning(PulumiProcessNames) is { Count: > 0 })
        {
            return "Pulumi is running now, and may be starting or installing this plugin";
        }

        var cache = Path.GetDirectoryName(Plugin.Folder)!;
        var findings = LiveTrees.FindLiveChildren([cache], ct);

        if (findings.Live.FirstOrDefault(live => live.Directory.Equals(Plugin.Folder, StringComparison.OrdinalIgnoreCase)) is { } live)
        {
            return $"{string.Join("; ", live.Holders)} is running from this plugin";
        }

        return findings.IsAnswered(cache) ? null : $"{LiveTreeVeto.CannotTell} plugin";
    }
}
