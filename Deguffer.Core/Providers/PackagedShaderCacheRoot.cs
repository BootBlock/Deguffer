using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A graphics vendor's directory as its driver writes it inside each packaged application's
/// container, and the children of it that <see cref="GpuShaderCacheProvider"/> recognises.
///
/// <para>A declaration of its own rather than a row of <see cref="GpuShaderCacheProvider.Roots"/>,
/// because there is no one place it is in. Each package that has rendered through the driver has its
/// own copy, so the paths come from <see cref="PackageContainers"/> and only the names are declared
/// here, once for every container.</para>
/// </summary>
/// <param name="DirectoryName">The vendor's directory inside the container.</param>
/// <param name="Children">
/// What may be deleted under it. Anything absent is Tier 4 by construction, in every container.
/// </param>
/// <param name="ProtectedNames">
/// Things in it that §5.6 must assert survived, as name and reason. See
/// <see cref="ShaderCacheRoot.ProtectedNames"/> for why these are separate from the children.
/// </param>
public sealed record PackagedShaderCacheRoot(
    string DirectoryName,
    DisposableChildSet Children,
    IReadOnlyList<(string Name, string Reason)> ProtectedNames);
