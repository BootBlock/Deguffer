using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where one media server keeps its transcoder's working files on this machine, and what it keeps
/// beside them.
/// </summary>
/// <param name="Roots">
/// The folders the transcoder's files are named in, each with the exact path under it that may be
/// emptied. Nothing else in them is ever reached.
/// </param>
/// <param name="Survivors">
/// What §5.6 asserts survived beyond what <paramref name="Roots"/> already name: the server's own data
/// where it sits outside every root, and anything withheld.
/// </param>
/// <param name="Notes">What the user is told about the settings, including anything left alone and why.</param>
/// <param name="ToolRoots">The same folders as §7.1 reads them. See <see cref="ICleanupProvider.ToolRoots"/>.</param>
/// <param name="LeftSomethingUnexamined">
/// Whether a folder the server may be using was withheld or could not be placed, so a plan with
/// nothing in it must not read as clear.
/// </param>
public sealed record MediaServerLayout(
    IReadOnlyList<DeclaredRoot> Roots,
    IReadOnlyList<(string Path, string Reason)> Survivors,
    IReadOnlyList<PlanNote> Notes,
    IReadOnlyList<ToolRoot> ToolRoots,
    bool LeftSomethingUnexamined)
{
    /// <summary>
    /// A folder a server's settings name, declared to §7.1 as one that holds nothing Explore may take.
    /// A drive's root is left out, for the reason <see cref="SpotifyCacheProvider"/> gives: refusing
    /// every child of a drive would take the whole drive away from Explore for one setting.
    /// </summary>
    public static IEnumerable<ToolRoot> Refusing(string folder, string reason) =>
        Path.GetDirectoryName(folder) is null ? [] : [ToolRoot.Of(folder, reason, new Safety.DisposableChildSet([]))];
}
