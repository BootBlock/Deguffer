using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Duplicates;

/// <summary>A file a walk kept, by its place in the tree, so no path is built until it has a match.</summary>
/// <param name="Route">The route that identifies the files of the volume it was found on.</param>
internal readonly record struct FoundFile(ExploreTree Tree, int Node, LocationRole Role, IdentityRoute Route);

/// <summary>
/// Goes through the tree of one searched location and keeps the files a search may match (§7.4).
///
/// <para><b>Applied as the tree is read</b>, so nothing the search leaves out is ever gathered: the
/// places passed over, links, empty files, hidden and system files where the search leaves them out,
/// the size and extension filters, and cloud files where the content is compared. A file's path is
/// built only once it has a match, because a drive holds millions of files and most have none.</para>
///
/// <para><b>Roles go down the tree.</b> A file takes the role of the innermost location holding it,
/// so a reference folder inside a searched drive stays a reference. A folder that matches a location
/// inside this one only when case is ignored is given the safer role, a reference, where either
/// role is one: the two can be different folders in a case-sensitive directory, and a file that
/// should have been a reference must never be offered for removal. For that reason too, the place a
/// reference location names is passed over where the location could not be resolved, because its
/// files would otherwise take the role of the location holding it (<see cref="UnresolvedReferences"/>).</para>
///
/// <para><b>What it could not read is named.</b> A folder Windows refused to list, and a location
/// read from a file table that was not read whole, would otherwise read as holding nothing that
/// matched, and a search that skipped a place must never read as one that found nothing there. A
/// place passed over is not gone through, so a refused folder inside one is not named again.</para>
/// </summary>
internal sealed class CandidateWalk
{
    private const string Refused =
        "Windows would not let Deguffer list this folder, so what it holds was not searched, or not all of it.";

    private const string TableIncomplete =
        "Part of this drive's file table could not be read, so some of the files here may not have been searched.";

    private readonly DuplicateSearch _search;
    private readonly UnresolvedReferences _unresolvedReferences;
    private readonly List<FoundFile> _found = [];
    private readonly List<PassedOverPlace> _passedOver = [];
    private readonly List<UnreadPlace> _unread = [];

    private int _links;
    private int _empty;
    private int _unknownLength;
    private int _onlyInTheCloud;

    /// <param name="unresolvedReferences">
    /// The places of the reference locations that were not resolved, passed over wherever the walk
    /// reaches one, because the role their files should take is what is not known.
    /// </param>
    public CandidateWalk(DuplicateSearch search, UnresolvedReferences unresolvedReferences)
    {
        _search = search;
        _unresolvedReferences = unresolvedReferences;
    }

    public IReadOnlyList<FoundFile> Found => _found;

    public IReadOnlyList<PassedOverPlace> PassedOver => _passedOver;

    public IReadOnlyList<UnreadPlace> Unread => _unread;

    public LeftOutFiles LeftOut => new(_links, _empty, _unknownLength, _onlyInTheCloud, Gone: 0, Unidentified: 0, ReadFailed: 0, Changed: 0);

    /// <summary>Note a place passed over before its tree was read, such as a whole location.</summary>
    public void PassOver(PassedOverPlace place) => _passedOver.Add(place);

    /// <param name="scan">The scan of <paramref name="root"/>'s folder: its tree, and its node in that tree.</param>
    /// <param name="within">The locations inside <paramref name="root"/>, whose roles its files may take.</param>
    /// <param name="below">What to pass over below the root, or null where nothing is passed over.</param>
    /// <param name="route">The route that identifies the files on the root's volume.</param>
    public void Read(
        ScannedFolder scan,
        ResolvedLocation root,
        IReadOnlyList<ResolvedLocation> within,
        PassedOverPlaces.Below? below,
        IdentityRoute route,
        CancellationToken ct)
    {
        var tree = scan.Tree;

        if (scan.FromIncompleteTable)
        {
            _unread.Add(new UnreadPlace(root.Folder, TableIncomplete));
        }

        var inner = within.ToLookup(location => location.Folder, StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(int Node, string Path, LocationRole Role)>();
        pending.Push((scan.Node, root.Folder, root.Role));

        while (pending.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();

            if (tree.ListingWasRefused(folder.Node))
            {
                _unread.Add(new UnreadPlace(folder.Path, Refused));
            }

            foreach (var child in tree.ChildrenOf(folder.Node))
            {
                var name = tree.NameOf(child);
                var path = Path.Join(folder.Path, name);

                if ((_unresolvedReferences.WhyPassedOver(path)
                        ?? below?.WhyPassedOver(folder.Path, folder.Node == scan.Node, path, name)) is { } why)
                {
                    _passedOver.Add(new PassedOverPlace(path, why));
                }
                else if (tree.IsLink(child))
                {
                    _links++;
                }
                else if (tree.IsDirectory(child))
                {
                    pending.Push((child, path, RoleOf(inner[path], path, folder.Role)));
                }
                else
                {
                    Consider(tree, child, name, folder.Role, route);
                }
            }
        }
    }

    private void Consider(ExploreTree tree, int file, string name, LocationRole role, IdentityRoute route)
    {
        var length = tree.LengthOf(file);

        if (length == 0)
        {
            // The file table gives a file it could not size no length at all, which is not an empty
            // file and must not be counted as one.
            if (tree.HasUnknownSizeBelow(file))
            {
                _unknownLength++;
            }
            else
            {
                _empty++;
            }

            return;
        }

        var visibility = tree.VisibilityOf(file);

        if ((visibility.HasFlag(FileVisibility.Hidden) && !_search.SearchHidden)
            || (visibility.HasFlag(FileVisibility.System) && !_search.SearchSystem)
            || !_search.Sizes.Admits(length)
            || !_search.Extensions.Admits(name))
        {
            return;
        }

        if (tree.StorageOf(file).HasFlag(FileStorage.CloudOnly) && _search.Criteria.ReadsContent())
        {
            _onlyInTheCloud++;
            return;
        }

        _found.Add(new FoundFile(tree, file, role, route));
    }

    /// <summary>
    /// The role of the folder at <paramref name="path"/>: that of the location it is, or of the folder
    /// holding it where it is none. See the class comment for a folder that is one only when case is
    /// ignored.
    /// </summary>
    private static LocationRole RoleOf(IEnumerable<ResolvedLocation> matching, string path, LocationRole inherited)
    {
        LocationRole? role = null;

        foreach (var location in matching)
        {
            if (location.Folder.Equals(path, StringComparison.Ordinal))
            {
                return location.Role;
            }

            role = location.Role == LocationRole.Reference || inherited == LocationRole.Reference || role == LocationRole.Reference
                ? LocationRole.Reference
                : LocationRole.Search;
        }

        return role ?? inherited;
    }
}
