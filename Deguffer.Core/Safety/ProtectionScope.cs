using Deguffer.Core.Exploring;

namespace Deguffer.Core.Safety;

/// <summary>How a <see cref="ProtectionScope"/> reaches one folder.</summary>
public enum FolderReach
{
    /// <summary>Nothing the protections are asked about is in the folder, holds it or is it.</summary>
    Outside,

    /// <summary>Something asked about may be in the folder or hold it, and no listing of it was made.</summary>
    Reached,

    /// <summary>A scan listed the whole folder, so what it holds is read from the scan's tree.</summary>
    Listed,
}

/// <summary>
/// How a <see cref="ProtectionScope"/> reaches one folder, and where it was listed if it was.
/// </summary>
/// <param name="Listing">The scan's tree, where <paramref name="Reach"/> is <see cref="FolderReach.Listed"/>.</param>
/// <param name="Node">The folder's node in <paramref name="Listing"/>.</param>
public readonly record struct ScopedFolder(FolderReach Reach, ExploreTree? Listing = null, int Node = -1);

/// <summary>
/// What a set of §7.1 protections will be asked about: every path, the paths inside a set of
/// folders, or what one Explore scan found below its root.
///
/// <para><b>So a discovery looks only where an answer can matter.</b> Finding the Chromium
/// user-data folders lists tens of thousands of directories below the two application-data roots,
/// and a policy built for a scan of another drive, or for a duplicate search of a music folder, is
/// never asked about anything there. Asked how it reaches a folder, a scope says whether nothing it
/// covers is there, whether something may be, or whether a scan has already listed the folder.</para>
///
/// <para><b>Outside is a promise the protections must still keep.</b> A discovery that does not
/// look in a folder declares it refused whole (<see cref="Providers.ChromiumUserDataProvider"/>), so
/// a policy asked about a path its scope said it would never see refuses it rather than allowing
/// what it never looked for.</para>
///
/// <para><b>A scan that ends without a tree reaches everything.</b> The page keeps showing whatever
/// it showed before, which may be any drive, so the protections built for it look everywhere.</para>
///
/// <para><b>Only a whole listing stands in for the disk.</b> A tree drawn from a file table that was
/// not read whole may lack any record, so a folder in it is never <see cref="FolderReach.Listed"/>,
/// and is walked as one the scope merely reaches.</para>
/// </summary>
public sealed class ProtectionScope
{
    private readonly IReadOnlyList<string>? _folders;
    private readonly Task<ExploreScan?>? _listing;
    private readonly IVolumeInventory? _volumes;

    private ProtectionScope(IReadOnlyList<string>? folders, Task<ExploreScan?>? listing, IVolumeInventory? volumes)
    {
        _folders = folders;
        _listing = listing;
        _volumes = volumes;
    }

    /// <summary>Every path on the machine, for protections whose questions are not known in advance.</summary>
    public static ProtectionScope Everything { get; } = new(folders: null, listing: null, volumes: null);

    /// <summary>The paths at and below <paramref name="folders"/>.</summary>
    /// <param name="volumes">Where each folder is followed to every path it is reachable at.</param>
    public static ProtectionScope Within(IEnumerable<string> folders, IVolumeInventory volumes)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(volumes);

        return new([.. folders], listing: null, volumes);
    }

    /// <summary>
    /// What one Explore scan of <paramref name="root"/> found, which is given once the scan ends:
    /// the scan, or null where it ended without one.
    /// </summary>
    public static ProtectionScope Scanned(string root, Task<ExploreScan?> listing, IVolumeInventory volumes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(volumes);

        return new([root], listing, volumes);
    }

    /// <summary>
    /// How this scope reaches <paramref name="folder"/>. Waits for a scan's tree where this is a
    /// scan's scope, because only the tree says whether the scan ended with one.
    /// </summary>
    public async Task<ScopedFolder> ReachAsync(string folder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        if (_folders is null || _volumes is null)
        {
            return new ScopedFolder(FolderReach.Reached);
        }

        if (_listing is not null)
        {
            if (await _listing.WaitAsync(ct).ConfigureAwait(false) is not { } scan)
            {
                return new ScopedFolder(FolderReach.Reached);
            }

            if (!scan.FromIncompleteTable && Listed(scan.Tree, folder) is { } node)
            {
                return new ScopedFolder(FolderReach.Listed, scan.Tree, node);
            }
        }

        var reached = ReachedFolder.At(folder, _volumes);

        return _folders.Any(inScope => ReachedFolder.At(inScope, _volumes) is var scoped && (scoped.Holds(reached) || reached.Holds(scoped)))
            ? new ScopedFolder(FolderReach.Reached)
            : new ScopedFolder(FolderReach.Outside);
    }

    /// <summary>
    /// The node of <paramref name="tree"/> that is <paramref name="folder"/>, found as
    /// <see cref="MftExploreReader.Locate"/> finds a folder, or null where the tree does not hold it,
    /// or holds it without having listed it: past a link the scan did not enter, or a folder that
    /// refused its listing. A walk of the disk asks about either itself.
    /// </summary>
    private int? Listed(ExploreTree tree, string folder)
    {
        if (ReachedFolder.At(tree.RootPath, _volumes!).PathTo(ReachedFolder.At(folder, _volumes!)) is not { } relative)
        {
            return null;
        }

        var (node, _) = MftExploreReader.Locate(tree, relative == "." ? [] : relative.Split(Path.DirectorySeparatorChar));

        return node is { } listed && !tree.ListingWasRefused(listed) ? listed : null;
    }
}
