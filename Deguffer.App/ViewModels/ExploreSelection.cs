using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What the user picked out by hand on the Explore page, and the four things §7.1 lets them do
/// with it.
///
/// <para>Separate from <see cref="ExploreViewModel"/> because the two have different subjects. That
/// one is about which node is being looked at and what the screen says about the drive; this one is
/// about one thing in it and what happens to that thing (G1). It decides nothing:
/// <see cref="ExploreActionPolicy"/> settles what may be removed, <see cref="ExploreRemovalPrompt"/>
/// what the user is told, and <see cref="ExploreRemovalReport.Summary"/> what happened — all in
/// Core, all provable without a WinUI host.</para>
/// </summary>
public sealed partial class ExploreSelection : ObservableObject
{
    private readonly ExploreActions _actions;

    /// <summary>
    /// Nodes removed since the scan.
    ///
    /// <para>The tree is parallel arrays built once, and rebuilding it would mean rescanning the
    /// drive — minutes, for one deleted folder. So what went is remembered instead: the list stops
    /// showing it, and <see cref="StaleNote"/> says plainly that the totals and the picture are now
    /// larger than what is on the disk. §7.1 allows Explore's numbers to be off provided the picture
    /// says which way, and this is the other direction of the same rule.</para>
    /// </summary>
    private readonly HashSet<int> _removed = [];

    private ExploreTree? _tree;

    /// <summary>
    /// The tree <see cref="_removed"/>'s indices belong to.
    ///
    /// <para>A node index means nothing outside the tree it came from, and the trees are replaced
    /// wholesale — on every mid-scan snapshot as well as at the end of a scan. Without this, a
    /// rescan filtered the new tree's children through the old tree's indices, so a directory that
    /// is genuinely on the disk vanished from the list and could not be opened, while the stale
    /// note claimed items had been removed since a scan that had only just started.</para>
    /// </summary>
    private ExploreTree? _removedFrom;

    private IReadOnlyList<int> _nodes = [];

    public ExploreSelection(ExploreActions actions) => _actions = actions;

    /// <summary>
    /// Whether the page is free to act. Set by the owner while a scan runs, because a removal and a
    /// scan of the same drive have no business overlapping.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeletePermanentlyCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand))]
    [NotifyCanExecuteChangedFor(nameof(PropertiesCommand))]
    public partial bool CanAct { get; set; } = true;

    /// <summary>Raised while a removal is under way, so the owner can stand its own commands down.</summary>
    public event EventHandler<bool>? Working;

    /// <summary>A sentence for the status line: what happened, or what another program refused.</summary>
    public event EventHandler<string>? Reported;

    /// <summary>Raised when something was removed, so the list and the picture are rebuilt.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// What is selected, named so the map's user can see it too.
    ///
    /// <para>The three pictures have no selection outline — the geometry is a bitmap, and a
    /// highlight would be a Core layout concern rather than a control one — so without this a
    /// right-click on a treemap tile would offer a menu about an item nothing on screen identified.
    /// The path is the identification.</para>
    /// </summary>
    public string Label => Items() switch
    {
        [] => string.Empty,
        [var only] => $"Selected: {only.Path} — {FreeSpace.Format(only.Bytes)}",
        var many => $"Selected: {many.Count} items — {FreeSpace.Format(many.Sum(i => i.Bytes))}",
    };

    /// <summary>
    /// Why the selection will not be removed, or null when nothing stands in the way.
    ///
    /// <para>Stated as soon as something is selected rather than after the user tries. §7.1 asks for
    /// refusals to be "stated with their reason rather than by greying something out", and a menu
    /// item that does nothing teaches nothing — least of all somebody reading a size picture, who
    /// has no way to guess which of several rules applies.</para>
    /// </summary>
    public string? Note =>
        Items() is [var first, ..] items
            ? items.Count == 1 ? Refusal(first) : Refusals(items)
            : null;

    public bool HasNote => Note is not null;

    /// <summary>
    /// How the picture now differs from the disk, or null while they still agree. See
    /// <see cref="_removed"/>.
    /// </summary>
    public string? StaleNote => !ReferenceEquals(_tree, _removedFrom) || _removed.Count == 0
        ? null
        : $"{_removed.Count} item(s) have been removed since this scan. The sizes above still count "
          + "them, and the map still draws them. Scan again for a current picture.";

    public bool HasStaleNote => StaleNote is not null;

    /// <summary>Whether this node has been removed, so the list should stop showing it.</summary>
    public bool WasRemoved(int node) =>
        ReferenceEquals(_tree, _removedFrom) && _removed.Contains(node);

    /// <summary>
    /// Point at a tree and select nothing. Called on every navigation, because a selection made in
    /// one folder is not a selection in the next one.
    /// </summary>
    public void Show(ExploreTree? tree)
    {
        _tree = tree;
        Select([]);
        Stale();
    }

    /// <summary>
    /// What the user picked out by hand, by node.
    ///
    /// <para>By node rather than by row, because the list is not the only view that can select. A
    /// map hit can land on a descendant several levels below the current node, which has no row at
    /// all — and §7.1's actions are the same actions whichever picture the user was reading.</para>
    ///
    /// <para>Never set by anything but a user gesture. §7.1: Explore never pre-selects, and never
    /// acts on more than what was picked out by hand.</para>
    /// </summary>
    public void Select(IReadOnlyList<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        _nodes = [.. nodes.Where(n => !WasRemoved(n))];

        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));

        DeleteCommand.NotifyCanExecuteChanged();
        DeletePermanentlyCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        RevealCommand.NotifyCanExecuteChanged();
        PropertiesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>§7.1's default: to the Recycle Bin, because recovery is available here.</summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task DeleteAsync(CancellationToken ct) => RemoveAsync(ExploreRemovalMode.RecycleBin, ct);

    /// <summary>
    /// §7.1's "deliberate second choice that says what it is". A separate command rather than a
    /// modifier on the one above, so what the user asked for is unambiguous by the time anything is
    /// deleted.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task DeletePermanentlyAsync(CancellationToken ct) => RemoveAsync(ExploreRemovalMode.Permanent, ct);

    [RelayCommand(CanExecute = nameof(CanActOnOne))]
    private void Open() => Announce(ShellActions.Open(_tree!.PathOf(_nodes[0])));

    [RelayCommand(CanExecute = nameof(CanActOnOne))]
    private void Reveal()
    {
        if (_tree is { } tree && _nodes is [var node])
        {
            Announce(ShellActions.Reveal(tree.PathOf(node), tree.IsDirectory(node)));
        }
    }

    [RelayCommand(CanExecute = nameof(CanActOnOne))]
    private void Properties() => Announce(ShellActions.Properties(_tree!.PathOf(_nodes[0])));

    private async Task RemoveAsync(ExploreRemovalMode mode, CancellationToken ct)
    {
        var items = Items();

        if (items.Count == 0)
        {
            return;
        }

        Working?.Invoke(this, true);

        try
        {
            if (await _actions.RemoveAsync(items, mode, ct) is not { } report)
            {
                // Declined. Saying so beats leaving the previous sentence standing, which somebody
                // who has just dismissed a dialog reads as the outcome of it.
                Reported?.Invoke(this, "Nothing was removed.");
                return;
            }

            // Recorded against the tree they are indices into, so a later tree cannot inherit them.
            if (!ReferenceEquals(_tree, _removedFrom))
            {
                _removed.Clear();
                _removedFrom = _tree;
            }

            foreach (var node in _nodes.Where(n => report.Removed.Any(r => IsNode(n, r.Path))))
            {
                _removed.Add(node);
            }

            Reported?.Invoke(this, report.Summary);
            Select([]);
            Stale();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            // The command is an AsyncRelayCommand without FlowExceptionsToTaskScheduler, so anything
            // escaping here is rethrown on the UI thread and takes the process down mid-deletion.
            // The Storage page's clean flow guards itself the same way and for the same reason.
            Reported?.Invoke(this, $"The removal stopped: {ex.Message}");
        }
        finally
        {
            Working?.Invoke(this, false);
        }
    }

    private void Stale()
    {
        OnPropertyChanged(nameof(StaleNote));
        OnPropertyChanged(nameof(HasStaleNote));
    }

    private bool IsNode(int node, string path) =>
        _tree is { } tree && string.Equals(tree.PathOf(node), path, StringComparison.OrdinalIgnoreCase);

    /// <summary>The selection as Core sees it. Empty while no scan is on screen.</summary>
    private IReadOnlyList<ExploreItem> Items() =>
        _tree is not { } tree
            ? []
            : [.. _nodes.Select(n => new ExploreItem(tree.PathOf(n), tree.IsDirectory(n), tree.SizeOf(n)))];

    private string? Refusal(ExploreItem item) =>
        _actions.Verdict(item.Path) is { IsAllowed: false } verdict ? verdict.Reason : null;

    private string? Refusals(IReadOnlyList<ExploreItem> items)
    {
        var reasons = items.Select(Refusal).OfType<string>().ToList();

        return reasons switch
        {
            [] => null,
            [var only] => $"One of these {items.Count} items will not be removed: {only}",
            _ => $"{reasons.Count} of these {items.Count} items will not be removed. Select them one "
                 + "at a time to see why.",
        };
    }

    /// <summary>
    /// What another program said went wrong, or nothing at all when it worked. A successful open
    /// leaves the status line alone: the window that appeared is the feedback.
    /// </summary>
    private void Announce(string? failure)
    {
        if (failure is not null)
        {
            Reported?.Invoke(this, failure);
        }
    }

    private bool CanRemove() => CanAct && _nodes.Count > 0 && _tree is not null;

    private bool CanActOnOne() => CanAct && _nodes.Count == 1 && _tree is not null;
}
