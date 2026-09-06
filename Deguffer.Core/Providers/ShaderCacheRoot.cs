using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Which of the profile's application-data tiers a shader-cache root sits in.
///
/// Two values because NVIDIA writes two separate caches, one in each of two tiers, and a row that
/// only named a directory could not tell them apart. A tier is named rather than a path held,
/// because <see cref="ShaderCacheRoot"/> stays a static declaration and the paths come from
/// <see cref="IUserEnvironment"/> at the moment they are needed.
/// </summary>
public enum ProfileArea
{
    /// <summary><c>%LOCALAPPDATA%</c>.</summary>
    LocalAppData,

    /// <summary><c>%USERPROFILE%\AppData\LocalLow</c>.</summary>
    LocalLowAppData,
}

/// <summary>
/// One graphics vendor's directory in one application-data tier, and the children of it that
/// <see cref="GpuShaderCacheProvider"/> recognises.
///
/// A row rather than a type per vendor, because what differs between vendors is only which
/// directory and which names — the tier, the consequence and the reasoning are one fact, held once
/// on the provider. Each row still carries its own <see cref="DisposableChildSet"/>, so §5.2's
/// question — which children may this tool delete? — is answered by reading one table.
/// </summary>
/// <param name="Area">
/// Which application-data tier the root sits in. NVIDIA has a row in each, because the driver keeps
/// a separate shader cache in both and they are of comparable size.
/// </param>
/// <param name="DirectoryName">
/// The root's name inside that tier, not a full path: the profile location comes from
/// <see cref="IUserEnvironment"/>, so this declaration stays static and testable against a
/// synthetic profile.
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
    ProfileArea Area,
    string DirectoryName,
    DisposableChildSet Children,
    IReadOnlyList<(string Name, string Reason)> ProtectedNames)
{
    /// <summary>
    /// What the user is shown this root called, qualified by tier only where it has to be.
    ///
    /// Derived rather than declared: two same-typed strings in every row would be a transposition
    /// waiting to happen, and until NVIDIA's second cache there was no distinction to draw at all.
    /// Qualification matters because every note this provider writes names the root, and two rows
    /// called "NVIDIA" would otherwise leave the user unable to tell which folder was meant.
    /// </summary>
    public string Label => Area is ProfileArea.LocalLowAppData
        ? DirectoryName + " (LocalLow)"
        : DirectoryName;

    /// <summary>
    /// Where this root is on <paramref name="environment"/>'s machine, or null when the tier itself
    /// could not be located — which only LocalLow can be, and which §5.2 says is not to be guessed
    /// at.
    /// </summary>
    public string? PathIn(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var area = Area switch
        {
            ProfileArea.LocalLowAppData => environment.LocalLowAppData,
            _ => environment.LocalAppData,
        };

        return area is null ? null : Path.Combine(area, DirectoryName);
    }
}
