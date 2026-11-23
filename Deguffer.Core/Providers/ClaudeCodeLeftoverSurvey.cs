using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Everything one look at Claude Code's folder found for the session leftovers row: its top level,
/// the evidence every kind of leftover was judged against, and each kind's classification.
/// </summary>
/// <param name="Root">What Claude Code's folder holds one level down, classified against <see cref="ClaudeCodeDerivedStateProvider.HomeChildren"/>.</param>
/// <param name="RootEntries">Everything at that level, for the files the walk does not see. Null where it refused to be listed.</param>
/// <param name="Evidence">What every kind was judged against.</param>
/// <param name="Looked">
/// Keeps anything written once the evidence was being read, as a guard the removal applies. See
/// <see cref="MinimumAge.Since"/>.
/// </param>
/// <param name="Kinds">One classification per kind of leftover.</param>
internal sealed record ClaudeCodeLeftoverSurvey(
    LevelWalk Root,
    IReadOnlyList<FileSystemInfo>? RootEntries,
    ClaudeCodeEvidence Evidence,
    MinimumAge Looked,
    IReadOnlyList<ClaudeCodeClassification> Kinds)
{
    /// <summary>
    /// Everything this look listed: Claude Code's folder, the walk of its top level, the project
    /// folders, and each folder a kind of leftover was classified in. Nearly every path the plan
    /// protects was returned by one of them, so §5.6's record of it is taken from there.
    /// </summary>
    public ListedEntries Listed()
    {
        var listed = new ListedEntries();

        if (RootEntries is not null)
        {
            listed.AddFolder(Evidence.Home, RootEntries);
        }

        listed.Include(Root.Listed);
        listed.Include(Evidence.Projects.Listed);

        foreach (var kind in Kinds)
        {
            listed.Include(kind.Listed);
        }

        return listed;
    }
}
