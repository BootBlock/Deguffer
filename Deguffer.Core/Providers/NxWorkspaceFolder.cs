using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A <c>.nx</c> folder beside an Nx workspace's <c>nx.json</c>, and the workspace.
///
/// <para><c>nx.json</c> is what says Nx made the folder: Nx puts <c>.nx</c> in the workspace root,
/// which is the folder holding <c>nx.json</c>, unless the workspace is Lerna's without one, which keeps
/// its cache in <c>node_modules\.cache\nx</c> instead (<c>pickCacheDirectory</c>). The folder also
/// holds things that are not cache: the index and daemon logs in <c>workspace-data</c>, and the Nx
/// wrapper's <c>nxw.js</c> and <c>installation</c>, which a workspace commits. So only its
/// <c>cache</c> is ever examined.</para>
/// </summary>
/// <param name="Path">The <c>.nx</c> folder.</param>
/// <param name="Workspace">The folder holding it and <c>nx.json</c>, which must survive (§5.6).</param>
internal sealed record NxWorkspaceFolder(string Path, string Workspace)
{
    public const string FolderName = ".nx";

    public const string Manifest = "nx.json";

    /// <summary>The folder Nx keeps a workspace's index and daemon logs in when it does not share them.</summary>
    public const string IndexName = "workspace-data";

    public string Name => System.IO.Path.GetFileName(Workspace);

    public string Cache => System.IO.Path.Combine(Path, NxCacheFolder.Name);

    public string Index => System.IO.Path.Combine(Path, IndexName);

    /// <summary>
    /// <paramref name="candidate"/> as a workspace's <c>.nx</c> folder, or null where it is not one: a
    /// link at it or at its parent, or no <c>nx.json</c> file beside it.
    /// </summary>
    public static NxWorkspaceFolder? TryRecognise(string candidate)
    {
        var path = candidate.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var workspace = System.IO.Path.GetDirectoryName(path);

        if (string.IsNullOrEmpty(workspace)
            || !System.IO.Path.GetFileName(path).Equals(FolderName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A junction here would let a deletion escape the folder that was examined.
        if (LongPath.IsReparsePoint(path) || LongPath.IsReparsePoint(workspace))
        {
            return null;
        }

        var manifest = System.IO.Path.Combine(workspace, Manifest);

        return LongPath.ProbeFile(manifest) == PathPresence.Present && !LongPath.IsReparsePoint(manifest)
            ? new NxWorkspaceFolder(path, workspace)
            : null;
    }
}
