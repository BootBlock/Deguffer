using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Which of the plugins Pulumi has installed a plan offers, and why each other one is held back.
///
/// <para>Every installed plugin is offered but one that something is using, one the check for use
/// could not answer for, and, where the route is Pulumi's command, one whose command would reach a
/// second folder Pulumi reads as the same plugin. While Pulumi runs, nothing is offered. See
/// <see cref="PulumiPluginUse"/> and <see cref="PulumiPluginTarget"/>, which ask the same questions
/// again at the clean.</para>
/// </summary>
/// <param name="Offered">The plugins the plan may remove.</param>
/// <param name="Held">Each installed plugin held back, with why, for §5.6.</param>
/// <param name="Notes">What the plan says about those held back.</param>
internal sealed record PulumiPluginChoice(
    IReadOnlyList<PulumiPlugin> Offered,
    IReadOnlyList<(string Path, string Reason)> Held,
    IReadOnlyList<PlanNote> Notes)
{
    /// <param name="cache">The cache as the plan read it.</param>
    /// <param name="byCommand">Whether each plugin is removed by <c>pulumi plugin rm</c> rather than by path.</param>
    /// <param name="pulumiRunning">Whether <c>pulumi</c> itself is running.</param>
    /// <param name="liveTrees">What reads where each program runs and works.</param>
    /// <param name="ct">Ends the reading of the process table.</param>
    public static PulumiPluginChoice Make(
        PulumiPluginCache cache,
        bool byCommand,
        bool pulumiRunning,
        ILiveTreeInspector liveTrees,
        CancellationToken ct)
    {
        if (cache.Installed.Count == 0)
        {
            return new PulumiPluginChoice([], [], []);
        }

        if (pulumiRunning)
        {
            return new PulumiPluginChoice(
                [],
                [.. cache.Installed.Select(plugin => (plugin.Folder, PulumiPluginUse.PulumiRunningReason))],
                [new PlanNote(PlanNoteSeverity.Warning, $"Left Pulumi's plugins alone: {PulumiPluginUse.PulumiRunningReason}")]);
        }

        var findings = liveTrees.FindLiveChildren([cache.Folder], ct);
        var answered = findings.IsAnswered(cache.Folder);
        var titles = cache.Installed.ToDictionary(plugin => plugin.Folder, plugin => plugin.Title, StringComparer.OrdinalIgnoreCase);

        var offered = new List<PulumiPlugin>();
        var held = new List<(string Path, string Reason)>();
        var notes = new List<PlanNote>();
        var cannotTell = 0;

        foreach (var plugin in cache.Installed)
        {
            if (findings.IsLive(plugin.Folder))
            {
                held.Add((plugin.Folder, PulumiPluginUse.InUseReason));
            }
            else if (!answered)
            {
                held.Add((plugin.Folder, LiveTreeVeto.UncheckedReason));
                cannotTell++;
            }
            else if (byCommand && cache.AlsoRemovedWith(plugin) is { } beside)
            {
                held.Add((plugin.Folder, "Pulumi's remove for this plugin would take another folder too, so it is left alone."));
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving {plugin.Title} alone: Pulumi reads {beside} as the same plugin, so its remove would take that folder too."));
            }
            else
            {
                offered.Add(plugin);
            }
        }

        if (LiveTreeVeto.NoteFor([.. findings.Live.Where(live => titles.ContainsKey(live.Directory))], live => titles[live.Directory]) is { } inUse)
        {
            notes.Insert(0, inUse);
        }

        if (LiveTreeVeto.UncheckedNote(cannotTell) is { } unanswered)
        {
            notes.Add(unanswered);
        }

        return new PulumiPluginChoice(offered, held, notes);
    }
}
