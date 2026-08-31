using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One graphics vendor's directory under <c>%LOCALAPPDATA%</c>, and the children of it that
/// <see cref="GpuShaderCacheProvider"/> recognises.
///
/// A row rather than a type per vendor, because what differs between vendors is only which
/// directory and which names — the tier, the consequence and the reasoning are one fact, held once
/// on the provider. Each row still carries its own <see cref="DisposableChildSet"/>, so §5.2's
/// question — which children may this tool delete? — is answered by reading one table.
/// </summary>
/// <param name="DirectoryName">
/// The root's name under <c>%LOCALAPPDATA%</c>, not a full path: the profile location comes from
/// <see cref="IUserEnvironment"/>, so this declaration stays static and testable against a
/// synthetic profile.
///
/// It is also the name the user is shown, because each vendor's directory is named after the
/// vendor. A separate display field would be two same-typed strings that are equal in every row,
/// which is a transposition waiting to happen and a distinction no vendor has yet needed.
/// </param>
/// <param name="Children">
/// What may be deleted under that root. Anything absent from it is Tier 4 by construction, which
/// is what makes "we did not recognise that" fail closed.
/// </param>
/// <param name="ProtectedNames">
/// Things in the root that §5.6 must assert survived, as name and reason.
///
/// Separate from <paramref name="Children"/> because a <see cref="DisposableChildSet"/> only ever
/// classifies a directory, and the thing most worth protecting in a tool root is usually a file —
/// which is never enumerated, never classified, and so never asserted unless it is named here.
/// A protected name is matched whether it is a file or a directory, because which one it is can
/// change between driver versions and the assertion should not.
///
/// An empty list is a claim rather than an omission: nothing else in that root has been established
/// as worth naming.
/// </param>
public sealed record ShaderCacheRoot(
    string DirectoryName,
    DisposableChildSet Children,
    IReadOnlyList<(string Name, string Reason)> ProtectedNames);
