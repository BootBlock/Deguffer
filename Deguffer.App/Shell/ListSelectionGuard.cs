using Deguffer.Core.Viewing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Deguffer.App.Shell;

/// <summary>
/// Tells a selection the reader made in a page's list from one the list made answering the page, for
/// the pages that rewrite rows under the reader: Explore and Memory. The rule is
/// <see cref="ListGestures"/>, and this hears the gestures it reads and keeps where the list stands.
///
/// <para>One per page rather than per list. Memory shows a list and a tree, only one of them on screen
/// at a time, and the question is whether the reader has touched the rows: there is one reader.</para>
/// </summary>
internal sealed class ListSelectionGuard
{
    /// <summary>Whether the view-model is rewriting the rows the list shows.</summary>
    private readonly Func<bool> _rewriting;

    /// <summary>Runs a restore once the list's report has returned, so the list is not written to from inside it.</summary>
    private readonly Action<Action> _later;

    /// <summary>How many of the page's own writes are in flight. See <see cref="Write"/>.</summary>
    private int _writing;

    /// <param name="rewriting">Whether the view-model is rewriting the rows the list shows.</param>
    /// <param name="later">Runs an action on the window's thread once the current event has returned.</param>
    public ListSelectionGuard(Func<bool> rewriting, Action<Action> later)
    {
        _rewriting = rewriting;
        _later = later;
    }

    /// <summary>Where the list stands between the rows it was given and the reader's hand on it.</summary>
    public ListGesture Gesture { get; private set; }

    /// <summary>
    /// The row container a gesture started in, or null where it landed outside one or on a button
    /// inside one (<see cref="RowHit"/>). A <c>TreeViewItem</c> is a <c>ListViewItem</c>, so this reads
    /// a tree's rows as well.
    /// </summary>
    public static ListViewItem? Row(object? source) =>
        source is DependencyObject element ? RowHit.Find(VisualTree.FromSelf(element), PartOf) as ListViewItem : null;

    /// <summary>
    /// Hear every gesture <paramref name="rows"/> is given, as it arrives and before the control acts
    /// on it.
    /// </summary>
    public void Hear(Control rows)
    {
        // Past the handled flag, because a ListViewItem marks a pointer press handled before an
        // ordinary handler on the control would see it.
        rows.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), handledEventsToo: true);

        // Every way a press ends, because the gesture is over whichever way it went and only one of
        // the three fires when the pointer is taken away from the rows mid-click: a capture loss when
        // something else claims it, and a cancellation when the touch is abandoned. A press whose end
        // goes unheard leaves the list Pressing, and every later settle is then refused.
        rows.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), handledEventsToo: true);
        rows.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnReleased), handledEventsToo: true);
        rows.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnReleased), handledEventsToo: true);

        // On the preview, because a ListView handles the arrow keys itself, and an ordinary KeyDown
        // handler would run after the selection had already moved.
        rows.PreviewKeyDown += (_, _) => Keyed();

        // The other moment a list settles on its own, and the one a rewrite does not cover: its
        // containers are built again when it comes back into the tree, on a return to a page held by
        // NavigationCacheMode.
        rows.Loaded += (_, _) => Settle();
    }

    /// <summary>
    /// Run one of the page's own writes to the list: its rows, its layout or its highlight. The list
    /// reports each step of it, and none of that is the reader's doing. The list has settled on what
    /// it was given once the write is over.
    /// </summary>
    public void Write(Action write)
    {
        _writing++;

        try
        {
            write();
        }
        finally
        {
            _writing--;
            Settle();
        }
    }

    /// <summary>
    /// The list has settled on rows or containers it was given, so whatever it reports next is its
    /// own doing, unless a press on a row is still held.
    /// </summary>
    public void Settle() => Gesture = Gesture.Settle();

    /// <summary>
    /// Whether a selection change the list reported is the reader's, to be taken as the selection.
    /// Where it is the list highlighting a row of its own accord, <paramref name="restore"/> is run
    /// once the report has returned, to put the highlight back on what is selected: leaving it
    /// standing would be the pre-selection §7.1 and §7.2 forbid, one screen further along, because
    /// the actions act on the view-model's selection rather than on the list's.
    /// </summary>
    public bool Admits(Action restore)
    {
        switch (Gesture.Judge(_writing > 0 || _rewriting()))
        {
            case ListReport.Accept:
                return true;

            case ListReport.Restore:
                _later(() =>
                {
                    if (Gesture.StillUntouched())
                    {
                        restore();
                    }
                });

                break;
        }

        return false;
    }

    /// <summary>A pointer went down in the list. See <see cref="ListGestures.Pressed"/>.</summary>
    internal void Pressed(bool onRow) => Gesture = ListGestures.Pressed(onRow);

    /// <summary>A key went down in the list. See <see cref="ListGestures.Keyed"/>.</summary>
    internal void Keyed() => Gesture = Gesture.Keyed();

    /// <summary>A press in the list ended. See <see cref="ListGestures.Released"/>.</summary>
    internal void Released() => Gesture = Gesture.Released();

    private static RowPart PartOf(DependencyObject element) => element switch
    {
        ButtonBase => RowPart.Button,
        ListViewItem => RowPart.Row,
        _ => RowPart.Content,
    };

    private void OnPressed(object sender, PointerRoutedEventArgs e) => Pressed(Row(e.OriginalSource) is not null);

    private void OnReleased(object sender, PointerRoutedEventArgs e) => Released();
}
