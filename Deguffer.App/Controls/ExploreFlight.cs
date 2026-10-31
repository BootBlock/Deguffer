using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Layout;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;

namespace Deguffer.App.Controls;

/// <summary>
/// The two pictures of a change of folder on the map: which set of drawings is the picture the map
/// works from and which the picture being left, which of the two is drawn over the other, and where
/// each is at each frame of the flight between them, for the phase the change is in
/// (<see cref="FolderFlight"/>).
///
/// <para>The phase is the map's to move on, because each move is part of what the map does with its
/// drawings, its zoom and its marks. What is left here is putting the two pictures where the phase
/// and the flight's clock (<see cref="ExploreDescent"/>) say.</para>
/// </summary>
internal sealed class ExploreFlight
{
    private readonly ContainerVisual _root;

    /// <summary>The control the pictures fill, read for its size at each frame.</summary>
    private readonly FrameworkElement _host;

    /// <summary>
    /// Put <paramref name="first"/>, the picture the map works from, and <paramref name="second"/> at
    /// the top of <paramref name="root"/>, the one over the other. Anything the map draws over both is
    /// put there after this.
    /// </summary>
    public ExploreFlight(ContainerVisual root, ExploreLayers first, ExploreLayers second, FrameworkElement host)
    {
        _root = root;
        _host = host;
        Pictures = first;
        Departing = second;

        _root.Children.InsertAtTop(Departing.Root);
        _root.Children.InsertAtTop(Pictures.Root);

        Descent.Moved += OnMoved;
    }

    /// <summary>The clock behind the flight. See <see cref="ExploreDescent"/>.</summary>
    public ExploreDescent Descent { get; } = new(SystemMotion.Current, RenderingClock.Current);

    /// <summary>
    /// The phase the change of folder on its way is in, and <see cref="FolderFlight.None"/> while there
    /// is none. The pointer is over nothing while it flies, because the screen is on its way from one
    /// picture to another (§7.1).
    /// </summary>
    public FolderFlight State { get; set; } = FolderFlight.None;

    /// <summary>
    /// The drawings of the picture the map works from. Swapped with <see cref="Departing"/> as the
    /// folder changes, so the old picture can go on showing while the camera flies from it to the new one.
    /// </summary>
    public ExploreLayers Pictures { get; private set; }

    /// <summary>The picture a change of folder is leaving, while it does, and empty otherwise.</summary>
    public ExploreLayers Departing { get; private set; }

    /// <summary>
    /// Hand the set of layers on screen to the picture being left, held where the camera had it at
    /// <paramref name="left"/>, and draw the new picture into the other set, which follows the camera,
    /// placed from <paramref name="origin"/>. The inner picture of <see cref="State"/> goes over the
    /// outer one: a folder opened grows over the picture it was in, and a folder left shrinks back into
    /// the picture it is in. Whatever the map draws over both stays over them.
    /// </summary>
    public void Swap(MapTransform left, MapOrigin origin)
    {
        (Pictures, Departing) = (Departing, Pictures);

        Departing.Freeze(left);
        Pictures.Follow();
        Pictures.Fit(_host.ActualWidth, _host.ActualHeight, origin);

        _root.Children.Remove(Departing.Root);

        if (State.Outward)
        {
            _root.Children.InsertAbove(Departing.Root, Pictures.Root);
        }
        else
        {
            _root.Children.InsertAtBottom(Departing.Root);
        }
    }

    /// <summary>The end of the change of folder: the picture left goes, and the picture worked from fills the screen.</summary>
    public void Land()
    {
        State = FolderFlight.None;

        Departing.Clear();
        Departing.Carry(MapFrame.Whole, 1, _host.ActualWidth, _host.ActualHeight);
        Pictures.Carry(MapFrame.Whole, 1, _host.ActualWidth, _host.ActualHeight);
    }

    /// <summary>
    /// One frame of the flight: the outer picture stretched so the inner folder's shape fills more of
    /// the map or less, and the inner folder's own drawing over it on the same frame, coming in or going.
    /// </summary>
    private void OnMoved(object? sender, EventArgs e)
    {
        var (inner, outer) = State.Outward ? (Departing, Pictures) : (Pictures, Departing);

        outer.Carry(Descent.Outer, 1, _host.ActualWidth, _host.ActualHeight);
        inner.Carry(Descent.Inner, Descent.Opacity, _host.ActualWidth, _host.ActualHeight);
    }
}
