using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Exploring.Network;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One node of the network browser's tree: a root, or a computer, share or folder under one. It
/// lists what is inside it only once it is opened, shows a ring while it does and a line of its own
/// where it fails, and stops listing once it is closed again.
///
/// <para>What a node lists, and what the filter keeps, are Core's (<see cref="NetworkTree"/>,
/// <see cref="NetworkMatch.Keeps"/>). The listing itself is the browser's, handed in, because the
/// browser holds the session every node reads through.</para>
/// </summary>
public sealed partial class NetworkNodeRow : ObservableObject
{
    private readonly Func<NetworkNodeRow, CancellationToken, Task<NetworkListing<NetworkPlace>>> _list;

    /// <summary>What the filter box holds now, read as each listing lands, so a node opened under a filter is narrowed too.</summary>
    private readonly Func<string> _filter;

    /// <summary>Everything listed inside, kept or not by the filter. <see cref="Children"/> is what the filter keeps.</summary>
    private List<NetworkNodeRow> _listed = [];

    /// <summary>The listing under way, cancelled as the node closes or is listed again.</summary>
    private CancellationTokenSource? _listing;

    /// <summary>Whether what is inside has been listed since the node last had to ask again.</summary>
    private bool _isListed;

    private NetworkNodeRow(
        NetworkRoot under,
        NetworkRoot? root,
        NetworkPlace? place,
        Func<NetworkNodeRow, CancellationToken, Task<NetworkListing<NetworkPlace>>> list,
        Func<string> filter)
    {
        Under = under;
        Root = root;
        Place = place;
        _list = list;
        _filter = filter;
    }

    /// <summary>A root of the tree.</summary>
    internal static NetworkNodeRow ForRoot(
        NetworkRoot root, Func<NetworkNodeRow, CancellationToken, Task<NetworkListing<NetworkPlace>>> list, Func<string> filter) =>
        new(root, root, null, list, filter);

    /// <summary>The root this node is under, which is where the browser finds its way back to it from.</summary>
    public NetworkRoot Under { get; }

    /// <summary>The root this node is, or null for a place.</summary>
    public NetworkRoot? Root { get; }

    /// <summary>The place this node is, or null for a root.</summary>
    public NetworkPlace? Place { get; }

    /// <summary>The place's path, which a choice fills the box with, or null for a root, which is no place to scan.</summary>
    public string? Path => Place?.Path;

    public string Name => Root is { } root ? NetworkText.Root(root) : NetworkText.Name(Place!);

    public string Glyph => Root is { } root ? NetworkGlyph.Of(root) : NetworkGlyph.Of(Place!.Kind);

    /// <summary>What a screen reader states: a root's name, or a place's kind and path.</summary>
    public string Spoken => Root is { } root ? NetworkText.Root(root) : NetworkText.Spoken(Place!);

    /// <summary>Whether this is a computer added by name, which can be taken out of the tree.</summary>
    public bool IsRemovable => Place is { } place && NetworkTree.CanRemove(Under, place);

    /// <summary>What the filter keeps of what is listed inside, brought up to date in place.</summary>
    public ObservableCollection<NetworkNodeRow> Children { get; } = [];

    /// <summary>
    /// The node listed inside this one on the way to <paramref name="target"/>, kept by the filter or
    /// not, by <see cref="NetworkTree.Toward"/>. How the browser finds its way back to where it was left.
    /// </summary>
    public NetworkNodeRow? Toward(string target) =>
        NetworkTree.Toward(_listed.Select(child => child.Place!), target) is { } next
            ? _listed.First(child => child.Place == next)
            : null;

    /// <summary>Whether this node is the place at <paramref name="target"/>, case aside. A root is no place, so never.</summary>
    public bool IsAt(string target) => Place?.Path.Equals(target, StringComparison.OrdinalIgnoreCase) is true;

    /// <summary>The listing under way or last finished, for whoever must wait for it: the browser finding its way back, and a test.</summary>
    public Task Listing { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Whether the tree should offer to open this node, which it does until a listing finds nothing inside.</summary>
    [ObservableProperty]
    public partial bool HasUnrealizedChildren { get; private set; } = true;

    /// <summary>Why the last listing found nothing, or null where it did not fail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FailureText), nameof(HasFailure), nameof(CanSignIn))]
    public partial NetworkFailure? Failure { get; private set; }

    public string FailureText => Failure is { } failure ? NetworkText.Failure(failure) : string.Empty;

    public bool HasFailure => Failure is not null;

    /// <summary>Whether the node offers the credentials prompt, by <see cref="NetworkTree.OffersSignIn"/>.</summary>
    public bool CanSignIn => NetworkTree.OffersSignIn(Place, Failure);

    /// <summary>
    /// Open the node and list what is inside, where it has not been listed. The tree opens a node by
    /// writing <see cref="IsExpanded"/>, which comes here too.
    /// </summary>
    /// <returns>The listing, done when what is inside is on screen.</returns>
    public Task OpenAsync()
    {
        IsExpanded = true;

        return Listing;
    }

    /// <summary>
    /// List this node again, and everything open inside it, because what was kept about it has been
    /// dropped. A node that is closed is only marked, and asks once it is opened.
    /// </summary>
    public void Relist()
    {
        Stop();
        _isListed = false;
        Failure = null;
        HasUnrealizedChildren = true;

        foreach (var child in _listed)
        {
            child.Relist();
        }

        if (IsExpanded)
        {
            Listing = ListAsync();
        }
    }

    /// <summary>
    /// Narrow what is on screen inside this node to <paramref name="filter"/>, and say whether the
    /// node itself is kept, by <see cref="NetworkMatch.Keeps"/>.
    /// </summary>
    public bool Narrow(string filter)
    {
        var kept = _listed.Where(child => child.Narrow(filter)).ToList();

        LiveList.Show(Children, kept, child => child.Place!.Path);

        return NetworkMatch.Keeps(Name, filter, kept.Count > 0);
    }

    /// <summary>Show that a sign-in failed, on this node, where the last listing failed.</summary>
    public void Failed(NetworkFailure failure) => Failure = failure;

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value)
        {
            // Closed while listing: stopped, and asked again once opened. Nothing listed is lost.
            Stop();
        }
        else if (!_isListed && !IsLoading)
        {
            Listing = ListAsync();
        }
    }

    private async Task ListAsync()
    {
        Stop();

        var listing = _listing = new CancellationTokenSource();
        var ct = listing.Token;
        IsLoading = true;

        try
        {
            var listed = await _list(this, ct);

            ct.ThrowIfCancellationRequested();
            Show(listed);
        }
        catch (OperationCanceledException)
        {
            // Closed, listed again, or the browser closed. Caught here because a listing runs from
            // the tree's handlers, where an exception escaping would end the process. A cancelled
            // listing changes nothing.
        }
        finally
        {
            if (ReferenceEquals(_listing, listing))
            {
                _listing = null;
                IsLoading = false;
            }
        }
    }

    /// <summary>Put what a listing found on screen, keeping each child that is still there as the same row.</summary>
    private void Show(NetworkListing<NetworkPlace> listed)
    {
        // Exact case, because a case-sensitive share can list Photos and photos side by side.
        var standing = _listed.ToDictionary(child => child.Place!.Path, StringComparer.Ordinal);

        _listed = [.. listed.Entries.Select(place => standing.GetValueOrDefault(place.Path) ?? new NetworkNodeRow(Under, null, place, _list, _filter))];
        _isListed = true;
        Failure = listed.Failure;
        HasUnrealizedChildren = _listed.Count > 0 || listed.Failure is not null;

        Narrow(_filter());
    }

    /// <summary>
    /// Cancel the listing under way. The source is not disposed: it has no timer to free, and the
    /// listing it stops may still be registering its token as it is cancelled.
    /// </summary>
    private void Stop()
    {
        if (_listing is null)
        {
            return;
        }

        _listing.Cancel();
        _listing = null;
        IsLoading = false;
    }
}
