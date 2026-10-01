namespace Deguffer.Core.Providers;

/// <summary>
/// The entries a plan targeted, each with the kind it targeted, for a provider whose
/// <see cref="ToolRoot"/> recognises exactly what its plan would take.
///
/// <para>The kind is kept as well as the path because a path alone answers for whatever stands there
/// when Explore asks. A session's transcript the plan offered as a file is not the same thing as a
/// folder or a link that later took its name, and the plan classified only the one it saw.</para>
/// </summary>
internal sealed class TargetedEntries
{
    private readonly Dictionary<string, ChildKind> _kinds = new(StringComparer.OrdinalIgnoreCase);

    public TargetedEntries(IEnumerable<DeletionTarget> targets)
    {
        foreach (var target in targets)
        {
            // A folder whose contents are cleared is still a folder standing where the plan found one.
            // A Recycle Bin is emptied through Windows and is nobody's child to recognise.
            ChildKind? kind = target.Kind switch
            {
                TargetKind.Directory or TargetKind.DirectoryContents => ChildKind.Folder,
                TargetKind.File => ChildKind.File,
                _ => null,
            };

            if (kind is { } known)
            {
                _kinds[target.Path] = known;
            }
        }
    }

    /// <summary>Whether <paramref name="child"/> of <paramref name="folder"/> is an entry the plan targeted, as what it targeted.</summary>
    public bool Recognises(string folder, ToolRootChild child) =>
        _kinds.TryGetValue(Path.Combine(folder, child.Name), out var kind) && kind == child.Kind;
}
