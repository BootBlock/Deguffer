using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Network;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The network browser the Path… box opens: a tree of the places this account uses or can see,
/// listed one level at a time as it is opened, with a computer added by name for the ones discovery
/// cannot find.
///
/// <para>One <see cref="NetworkLookup"/> for its life, so a node opened twice asks once, and closing
/// the browser cancels whatever is still asking. Picking a place only says which path to fill the
/// box with: the page's own check still asks whether it is a folder, and nothing about removal
/// changes (§7.1).</para>
///
/// <para>What the tree holds and what the filter keeps are Core's (<see cref="NetworkTree"/>,
/// <see cref="NetworkMatch"/>). This wires the nodes to the session and to the preferences, where
/// the added computers and the place it was left at are kept.</para>
/// </summary>
public sealed partial class NetworkBrowser : ObservableObject, IDisposable
{
    private readonly NetworkLookup _lookup;
    private readonly PreferenceService _preferences;

    /// <summary>The window the credentials prompt is owned by.</summary>
    private readonly nint _owner;

    /// <param name="network">The sources the tree is listed from.</param>
    /// <param name="time">What each listing's time limit is measured on.</param>
    /// <param name="preferences">Where the added computers and the place the browser was left at are kept.</param>
    /// <param name="owner">The window the credentials prompt is owned by.</param>
    internal NetworkBrowser(NetworkAccess network, TimeProvider time, PreferenceService preferences, nint owner)
    {
        _lookup = new NetworkLookup(network, time);
        _preferences = preferences;
        _owner = owner;

        foreach (var root in NetworkTree.Roots)
        {
            Roots.Add(NetworkNodeRow.ForRoot(root, ListAsync, () => Filter));
        }
    }

    public ObservableCollection<NetworkNodeRow> Roots { get; } = [];

    /// <summary>The node selected in the tree, whose path the path bar shows and a choice uses.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPath), nameof(CanUse), nameof(Choice))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial NetworkNodeRow? Selected { get; set; }

    /// <summary>The selected place's full path, or empty where a root or nothing is selected.</summary>
    public string SelectedPath => Selected?.Path ?? string.Empty;

    /// <summary>
    /// Whether the selection is a folder the box can be filled with (<see cref="NetworkPlace.IsFolder"/>).
    /// A root is a heading, and a computer holds shares rather than being one.
    /// </summary>
    public bool CanUse => Selected?.Place?.IsFolder is true;

    /// <summary>What a choice fills the box with, or null where the selection is no folder.</summary>
    public string? Choice => CanUse ? Selected!.Path : null;

    /// <summary>What narrows the nodes already listed, by name.</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    /// <summary>A computer's name or address, typed to add it to the tree.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddComputerCommand))]
    public partial string ComputerText { get; set; } = string.Empty;

    /// <summary>Why the computer typed was not added, or null where nothing is wrong.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComputerRefusal))]
    public partial string? ComputerRefusal { get; private set; }

    public bool HasComputerRefusal => ComputerRefusal is not null;

    /// <summary>
    /// Open the tree where it was left: the root and the place it was last left at, opened one level
    /// at a time as each is listed. The first time, the roots that list in a moment are opened
    /// (<see cref="NetworkTree.OpensAtFirst"/>).
    /// </summary>
    /// <returns>Done once the place it was left at is selected, or once the way to it has gone.</returns>
    public async Task ShownAsync()
    {
        var locations = _preferences.Current.Locations;

        if (locations is not { LastRoot: { } root, LastPlace: { } place })
        {
            await Task.WhenAll(Roots.Where(row => NetworkTree.OpensAtFirst(row.Root!.Value)).Select(row => row.OpenAsync()));

            return;
        }

        var node = RootRow(root);

        // Down one level for each listing, until the place is reached or the way to it has gone, in
        // which case the deepest node still on the way is selected.
        while (!node.IsAt(place))
        {
            await node.OpenAsync();

            if (node.Toward(place) is not { } next)
            {
                break;
            }

            node = next;
        }

        Selected = node;
    }

    /// <summary>Keep where the browser was left, so it opens there next time. Called as it closes, however it closes.</summary>
    public void Closing()
    {
        if (Selected is { Path: { } path } selected)
        {
            _preferences.Update(current => current with { Locations = current.Locations.LeftAt(selected.Under, path) });
        }
    }

    /// <summary>List <paramref name="node"/> again, or the whole tree where none is given.</summary>
    [RelayCommand]
    private void Refresh(NetworkNodeRow? node)
    {
        if (node is null)
        {
            _lookup.ForgetAll();

            foreach (var root in Roots)
            {
                root.Relist();
            }

            return;
        }

        if (node.Place is { } place)
        {
            _lookup.Forget(place);
        }
        else
        {
            _lookup.Forget(node.Under);
        }

        node.Relist();
    }

    /// <summary>
    /// Add the computer typed to <see cref="NetworkRoot.Added"/>, keep it in the preferences, and
    /// list its shares, which is what reaches a computer discovery cannot find.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddComputer))]
    private async Task AddComputerAsync()
    {
        ComputerRefusal = null;

        if (NetworkNames.ComputerName(ComputerText) is not { } name)
        {
            ComputerRefusal = NetworkText.NotAComputer;

            return;
        }

        if (!_preferences.Update(current => current with { Locations = current.Locations.WithComputer(name) }))
        {
            ComputerRefusal = NetworkText.NotKept;

            return;
        }

        ComputerText = string.Empty;

        var added = AddedRoot;
        added.Relist();
        await added.OpenAsync();

        if (added.Children.FirstOrDefault(child => child.Place!.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } computer)
        {
            Selected = computer;
            await computer.OpenAsync();
        }
    }

    private bool CanAddComputer() => !string.IsNullOrWhiteSpace(ComputerText);

    /// <summary>Take an added computer out of the tree and the preferences.</summary>
    [RelayCommand]
    private void RemoveComputer(NetworkNodeRow computer)
    {
        if (!computer.IsRemovable
            || !_preferences.Update(current => current with { Locations = current.Locations.WithoutComputer(computer.Place!.Name) }))
        {
            return;
        }

        if (ReferenceEquals(Selected, computer))
        {
            Selected = AddedRoot;
        }

        AddedRoot.Relist();
    }

    /// <summary>
    /// Sign in to a node that refused this account, through the Windows credentials prompt, so
    /// Deguffer never sees or stores the password. Listed again once connected.
    /// </summary>
    [RelayCommand]
    private async Task SignInAsync(NetworkNodeRow node)
    {
        if (node.Place is not { } place)
        {
            return;
        }

        var signIn = await _lookup.SignInAsync(place, _owner, CancellationToken.None);

        switch (signIn.Outcome)
        {
            case SignInOutcome.Connected:
                node.Relist();
                break;

            case SignInOutcome.Failed:
                node.Failed(signIn.Failure!.Value);
                break;
        }
    }

    public void Dispose() => _lookup.Dispose();

    partial void OnFilterChanged(string value)
    {
        foreach (var root in Roots)
        {
            root.Narrow(value);
        }
    }

    private NetworkNodeRow AddedRoot => RootRow(NetworkRoot.Added);

    private NetworkNodeRow RootRow(NetworkRoot root) => Roots.First(row => row.Root == root);

    private Task<NetworkListing<NetworkPlace>> ListAsync(NetworkNodeRow node, CancellationToken ct) =>
        node.Root is { } root
            ? _lookup.RootAsync(root, _preferences.Current.Locations, ct)
            : _lookup.ChildrenAsync(node.Place!, ct);
}
