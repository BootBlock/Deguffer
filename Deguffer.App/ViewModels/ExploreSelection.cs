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
/// <see cref="ExploreActions"/> settles what may be removed and carries it out,
/// <see cref="ExploreRefusalNote"/> says why a selection will not be, <see cref="ExploreRemovals"/>
/// what has gone since the scan, and <see cref="ExplorePlace.TryCarry"/> what a selection still
/// names in a later tree — all in Core, all provable without a WinUI host.</para>
/// </summary>
public sealed partial class ExploreSelection : ObservableObject
{
    private readonly ExploreActions _actions;

    /// <summary>
    /// What has gone since the scan. The list stops showing it, and <see cref="StaleNote"/> says
    /// plainly that the totals and the picture are now larger than what is on the disk.
    /// </summary>
    private readonly ExploreRemovals _removals = new();

    private ExploreTree? _tree;

    private IReadOnlyList<int> _nodes = [];
    private string _label = string.Empty;
    private string _figures = string.Empty;
    private string? _note;

    public ExploreSelection(ExploreActions actions)
    {
        _actions = actions;

        // Started here rather than on the first selection, because §5.2's probed half takes a moment
        // and the page has nothing else to do while it opens. Until it lands every path is refused
        // with a sentence saying why, and this is what makes that window short enough to go unseen.
        //
        // A build that has already finished announces itself inside Prepare, before the owner has
        // subscribed to anything here. Nothing is lost by that: nothing can be selected yet, so the
        // restatement has nothing to say, and the first selection asks the finished policy.
        _actions.Ready += (_, _) => Restate();
        _actions.Prepare();
    }

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
    /// What is selected, by node.
    ///
    /// <para>Named so the list and the map can be put back in step with it. A <c>ListView</c> keeps
    /// its own copy of the selection and drops an item from it when the collection under it stops
    /// holding that item where it was, then reports that back as though the user had cleared it — so
    /// something has to say which of the two copies is right, and it is this one. The map holds no
    /// copy at all: it is told what to outline.</para>
    ///
    /// <para>Raises a change of its own, which is the one signal every screen that shows the
    /// selection can follow. Without it each caller of <see cref="Select"/> has to remember to tell
    /// each of them, and the one that gets forgotten is a highlight left on something that is no
    /// longer selected.</para>
    /// </summary>
    public IReadOnlyList<int> Nodes => _nodes;

    /// <summary>
    /// What is selected, in words.
    ///
    /// <para>The only thing that <em>names</em> what is selected. The map draws a line round the
    /// shape, which says which one; this says which folder. A menu offering to delete something has
    /// to answer the second question as well as the first.</para>
    /// </summary>
    public string Label => _label;

    /// <summary>
    /// How big it is, said apart from what it is.
    ///
    /// <para>Split from <see cref="Label"/> for the reason
    /// <see cref="ExploreViewModel.HoveredFigures"/> gives: the two share one line, and a path long
    /// enough to fill it would otherwise push the size off the end.</para>
    /// </summary>
    public string Figures => _figures;

    /// <summary>
    /// Why the selection will not be removed, or null when nothing stands in the way. See
    /// <see cref="ExploreRefusalNote"/>.
    /// </summary>
    public string? Note => _note;

    public bool HasNote => _note is not null;

    /// <summary>
    /// How the picture now differs from the disk, or null while they still agree. See
    /// <see cref="_removals"/>.
    /// </summary>
    public string? StaleNote => _removals.CountIn(_tree) is > 0 and var count
        ? $"{count} item(s) have been removed since this scan. The sizes above still count them, and "
          + "the map still draws them. Scan again for a current picture."
        : null;

    public bool HasStaleNote => StaleNote is not null;

    /// <summary>
    /// Whether this node has gone since the scan, so nothing on screen may offer it.
    ///
    /// <para>The list stops showing such a node; the map cannot, because the tree behind the
    /// picture is not rebuilt for a deletion — see <see cref="_removals"/> — so the shape stays where
    /// it was. What both must stop doing is <em>acting</em> on it, and the map must stop marking it
    /// out under the pointer, which reads as an offer to pick something that can only select
    /// nothing (§7.1). Asked of the tree on screen: see <see cref="ExploreRemovals.WasRemoved"/>.</para>
    /// </summary>
    public bool WasRemoved(int node) => _removals.WasRemoved(_tree, node);

    /// <summary>
    /// Ask what may be removed again, because part of the answer is about this minute: a folder an
    /// installer had open when the page opened, an entry a program has started working in since.
    /// Called at the start of a scan, which is when everything else on the page is re-measured too.
    /// </summary>
    public void Reconsider() => _actions.Reconsider();

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
    /// Move to a tree that continues the one on screen, keeping whatever is still selected.
    ///
    /// <para>The other half of <see cref="Show"/>, and what separates them is what the replacement
    /// meant. Stepping into a folder is a new subject, so the selection goes with the old one; a
    /// snapshot arriving mid-scan is the same subject measured again, and dropping the selection
    /// every time one lands is what made a folder impossible to pick while a scan ran.</para>
    ///
    /// <para>A node is kept only where it still names what it named, which is
    /// <see cref="ExplorePlace.TryCarry"/>'s rule and the same one that decides where the page is
    /// standing. One that does not carry is dropped rather than replaced: §7.1 lets this act only on
    /// what the user picked out by hand, and putting something else in its place would be the tool
    /// choosing the target.</para>
    /// </summary>
    public void Carry(ExploreTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        // Read against the tree the numbers belong to, before that becomes the arriving one.
        var kept = _nodes.Where(node => ExplorePlace.TryCarry(_tree, node, tree) is not null).ToArray();

        _tree = tree;

        Select(kept);
        Stale();
    }

    /// <summary>
    /// What the user picked out by hand, by node.
    ///
    /// <para>By node rather than by row, because the list is not the only view that can select. A
    /// map hit can land on a descendant several levels below the current node, which has no row at
    /// all — and §7.1's actions are the same actions whichever picture the user was reading.</para>
    ///
    /// <para>§7.1: Explore never pre-selects, and never acts on more than what was picked out by
    /// hand. A gesture is the only thing that may widen this, and the page holds every change the
    /// list reports while the rows are being rewritten, so what a rewrite left behind never arrives
    /// here as one. The callers that are not gestures only ever narrow: <see cref="Show"/> empties
    /// it outright, the end of a removal empties it or drops what the removal took, and
    /// <see cref="Carry"/> drops whatever no longer names what it named.</para>
    /// </summary>
    public void Select(IReadOnlyList<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        _nodes = [.. nodes.Where(n => !WasRemoved(n))];

        // Worked out once, here, rather than on each read. Each of the three walks the selection
        // rebuilding a path per node, and the note asks the policy for a verdict on top of that, so
        // computing them lazily does the same work three times for every change — over a list view
        // that selects any number of rows at once (G4).
        var items = Items();

        (_label, _figures) = items switch
        {
            [] => (string.Empty, string.Empty),
            [var only] => ($"Selected: {only.Path}", FreeSpace.Format(only.Bytes)),
            var many => ($"Selected: {many.Count} items", FreeSpace.Format(many.Sum(i => i.Bytes))),
        };

        _note = NoteFor(items);

        OnPropertyChanged(nameof(Nodes));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Figures));
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
        // Taken together and before the await, because nothing stops the list, the map or the trail
        // while the removal runs: navigating empties the selection, and a pick replaces it. What is
        // recorded has to be what was acted on, or a removed folder stays listed and pickable.
        var (tree, picked, items) = (_tree, _nodes, Items());

        if (tree is null || items.Count == 0)
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

            _removals.Record(tree, picked, report);

            Reported?.Invoke(this, report.Summary);

            // Every Select replaces the list, so the same list means nothing was picked meanwhile and
            // what was acted on is let go. A pick made while this ran is the user's and stays, less
            // anything the removal took from under it.
            Select(ReferenceEquals(_nodes, picked) ? [] : _nodes);
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

    /// <summary>The selection as Core sees it. Empty while no scan is on screen.</summary>
    private IReadOnlyList<ExploreItem> Items() =>
        _tree is not { } tree
            ? []
            : [.. _nodes.Select(n => new ExploreItem(tree.PathOf(n), tree.IsDirectory(n), tree.SizeOf(n)))];

    /// <summary>
    /// Ask the policy about the current selection again, and say so if the answer changed.
    ///
    /// <para>The note is the one thing on this page whose answer arrives late. Everything else is
    /// derived from the tree, which is on screen before anything is selected; the refusal waits on
    /// the providers, and on a rebuild it waits on them again — so a selection made while
    /// <see cref="ExploreActions"/> was still asking is told what the answer turned out to be
    /// rather than left holding "in a moment".</para>
    /// </summary>
    private void Restate()
    {
        _note = NoteFor(Items());

        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
    }

    private string? NoteFor(IReadOnlyList<ExploreItem> items) => ExploreRefusalNote.For(items, _actions.Verdict);

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
