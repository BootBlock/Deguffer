using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// A §5.2 declaration as <see cref="ExploreActionPolicy"/> asks it: the root resolved, and followed
/// to every path it is reachable at.
///
/// <para><b>A provider names its root the way its tool or a setting does</b>, and the item asked about
/// is named the way the user reached it. With <c>S:</c> substituted for <c>C:\Users\testuser\src</c>, a
/// vcpkg clone a tool reports at <c>S:\vcpkg</c> holds <c>C:\Users\testuser\src\vcpkg\installed</c>,
/// and compared as text it held nothing, so the clone's refusal of <c>installed</c> did not apply.
/// <see cref="VolumeRoot.Places"/> follows a letter to the folder it stands for and never back, so
/// both sides are followed.</para>
///
/// <para><b>Followed once, when the policy is built.</b> A policy asks about every row a selection
/// touches, and a declaration is one of hundreds. The item is followed at each question, which is what
/// keeps a volume mounted after the policy was built covered on that side.</para>
/// </summary>
/// <param name="Path">The root in display form, as <see cref="LongPath.Configured(string?)"/> resolves it.</param>
internal sealed record DeclaredRoot(ToolRoot Root, string Path, ReachedFolder Folder)
{
    /// <summary>Each of <paramref name="roots"/> that resolves, followed through <paramref name="volumes"/>.</summary>
    public static IReadOnlyList<DeclaredRoot> Follow(IEnumerable<ToolRoot> roots, IVolumeInventory volumes) =>
    [
        .. roots
            .Select(root => (Root: root, Path: LongPath.Configured(root.Path)))
            .Where(root => root.Path is not null)
            .Select(root => new DeclaredRoot(root.Root, root.Path!, ReachedFolder.At(root.Path!, volumes))),
    ];

    /// <summary>
    /// <paramref name="target"/> named below <see cref="Path"/>, or null where this root does not hold
    /// it at any path either is reachable at.
    /// </summary>
    public string? Naming(ReachedFolder target) => Folder.Naming(target, Path);
}
