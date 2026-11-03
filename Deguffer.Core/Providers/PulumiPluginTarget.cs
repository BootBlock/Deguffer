using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether <c>pulumi plugin rm</c> for one plugin, run now, would still remove that plugin alone while
/// nothing is using it.
///
/// <para><b>The command names a plugin, and Pulumi finds the folders when it runs.</b> It lists the
/// cache afresh and removes every folder whose kind, name and version match, the version read
/// leniently. A folder written since the preview that Pulumi reads as the same plugin would go with
/// it, unexamined, so the cache is listed again and the plugin offered only where it is still the one
/// folder that matches.</para>
///
/// <para><b>And the folder is removed with Go's <c>os.RemoveAll</c>.</b> A cache that has become a link
/// since the preview is not the folder this examined, so the step does not run through one. A plugin's
/// own folder replaced by a link is no longer an installed plugin in the new listing, which never
/// counts a link as one.</para>
/// </summary>
internal sealed class PulumiPluginTarget(PulumiPluginUse use) : ICommandTargetCheck
{
    public string? WhyNot(RunCommandStep step, CancellationToken ct)
    {
        var plugin = use.Plugin;
        var cache = Path.GetDirectoryName(plugin.Folder)!;

        if (LongPath.IsReparsePoint(cache))
        {
            return $"{cache} is now a link to somewhere else, and Pulumi's remove would follow it";
        }

        if (PulumiPluginCache.Read(cache) is not { } now)
        {
            return $"Windows would not list {cache}, so Deguffer cannot tell what Pulumi's remove would reach";
        }

        if (!now.Installed.Contains(plugin))
        {
            return $"{plugin.Folder} is no longer an installed plugin";
        }

        if (now.AlsoRemovedWith(plugin) is { } beside)
        {
            return $"Pulumi now reads {beside} as the same plugin, and its remove would take that folder too";
        }

        return use.WhyInUse(ct);
    }
}
