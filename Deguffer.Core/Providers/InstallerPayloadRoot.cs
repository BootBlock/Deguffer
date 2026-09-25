using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One folder an installer leaves its payloads in, and the rule that says which of its children are
/// payloads.
///
/// <para>A row rather than a type per vendor, for the reason <see cref="ShaderCacheRoot"/> is one:
/// the tier, the consequence and the reasoning belong to the <see cref="InstallerPayloadProvider"/>
/// that declares the row, and what differs between vendors is which folder and which children, which
/// is data.</para>
///
/// <para><b>The row names its folder from a base it never lists.</b> <c>C:\NVIDIA</c>,
/// <c>C:\AMD</c> and <c>C:\Autodesk</c> sit at the top of the system drive, beside
/// <c>Program Files</c>, <c>Users</c> and <c>Windows</c>, and nothing may ever be classified there.
/// So <see cref="Base"/> is reached by name only, every folder between it and <see cref="Path"/> is
/// walked by name as well, and only <see cref="Path"/> itself is listed.</para>
/// </summary>
/// <param name="Label">
/// What the user is shown this folder called in a note, such as <c>NVIDIA\DisplayDriver</c>. Also
/// the prefix of every item's identity, so two vendors' children of the same name stay apart.
/// </param>
/// <param name="Vendor">Whose installer wrote the folder, shown beside each item.</param>
/// <param name="Base">
/// The system directory the folder sits in: the top of the system drive, or <c>%PROGRAMDATA%</c>.
/// Never listed and never a target.
/// </param>
/// <param name="RelativePath">Where the folder sits below <paramref name="Base"/>.</param>
/// <param name="Classify">
/// What one child of the folder is, given the folder's path and the child's name. Anything the rule
/// does not recognise is Tier 4, which is the direction §5.2 requires the unknown case to fail in.
/// </param>
/// <param name="ProtectedNames">
/// Children §5.6 must assert survived, as name and reason. A file is never listed, so naming it here
/// is the only way it is ever asserted; a directory is named where losing it breaks something
/// outside this folder, so the evidence names it rather than folding it into "unrecognised".
/// </param>
public sealed record InstallerPayloadRoot(
    string Label,
    string Vendor,
    string Base,
    string RelativePath,
    Func<string, string, ChildClassification> Classify,
    IReadOnlyList<(string Name, string Reason)> ProtectedNames)
{
    public string Path => System.IO.Path.Combine(Base, RelativePath);

    /// <summary>
    /// Every folder between <see cref="Base"/> and <see cref="Path"/>, outermost first. Each is
    /// reached by name, checked for being a link, and asserted to have survived.
    /// </summary>
    public IReadOnlyList<string> Containers
    {
        get
        {
            var segments = RelativePath.Split(
                [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            var containers = new List<string>(segments.Length);
            var current = Base;

            for (var i = 0; i < segments.Length - 1; i++)
            {
                current = System.IO.Path.Combine(current, segments[i]);
                containers.Add(current);
            }

            return containers;
        }
    }

    /// <summary>Whether a child of <see cref="Path"/>, given by name, is a payload this row recognises.</summary>
    public bool Recognises(string name) => Classify(Path, name).Tier.IsOfferable();
}
