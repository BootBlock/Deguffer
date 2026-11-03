using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// The rule a Berry cache was offered on, asked again at the clean: everything in it is still a file
/// Yarn writes there.
///
/// <para>The cache goes whole, so a folder, a link or a file of somebody else's put into it after the
/// preview would go with it, under a rule that never named it (§5.2). Read again from the disk, an
/// entry like that holds the step back, as it would have kept the cache out of the plan.</para>
/// </summary>
/// <param name="globalFolder">The global folder whose cache the step removes.</param>
internal sealed class YarnBerryCacheCheck(string globalFolder) : IUseCheck
{
    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
    {
        var now = YarnBerryFolder.Read(globalFolder);

        return now.State switch
        {
            YarnBerryCacheState.Stranger =>
                [new InUseNow(step.Path, $"it now holds '{now.Cause}', which Yarn does not write there")],
            YarnBerryCacheState.Link =>
                [new InUseNow(step.Path, $"'{now.Cause}' is now a link to somewhere else")],
            YarnBerryCacheState.Refused =>
                [new InUseNow(step.Path, $"Windows would no longer let Deguffer read all of '{now.Cause}'")],

            // Gone or emptied since the preview: nothing is left that the rule does not name.
            _ => [],
        };
    }
}
