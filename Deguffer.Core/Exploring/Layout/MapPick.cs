using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// What a click, a right-click or the keyboard landing on a shape picks on the map: a node, or nothing,
/// which clears the selection.
///
/// <para>Neither block picks anything. The one standing in for items too small to draw is several
/// thousand files at once, and §7.1 has no bulk action, and free space is not on the disk to be acted
/// on. A picture that answers nothing, such as a step of a replay of what grew
/// (<see cref="MapReading.Answers"/>), picks nothing at all, so a click on it leaves the selection as it
/// is rather than clearing it.</para>
///
/// <para>In Core rather than in the shell, as <see cref="MapPress"/> is, so the rules are provable
/// without a window (G8). Whether the press may pick at all is <see cref="MapPress"/>'s to say.</para>
/// </summary>
/// <param name="Node">The node picked, or null where the pick is of nothing.</param>
public readonly record struct MapPick(int? Node)
{
    /// <summary>
    /// What a click over <paramref name="hit"/> picks, on a picture that <paramref name="answers"/> or
    /// not, while the map is <paramref name="pictured"/> or not; or null where the click says nothing.
    /// </summary>
    /// <param name="answers">Whether the picture on the page answers the pointer (<see cref="MapReading.Answers"/>).</param>
    /// <param name="pictured">
    /// Whether anything is on screen to click: a drawing, or some of a redraw that has landed. A click with
    /// no picture yet leaves the selection as it is. A click on a picture still landing picks what has
    /// landed under it, as the readout already names.
    /// </param>
    /// <param name="hit">What is under the click, or null where it is over nothing.</param>
    public static MapPick? Click(bool answers, bool pictured, ExploreHit? hit) =>
        pictured ? Landing(answers, hit) : null;

    /// <summary>
    /// What landing on <paramref name="hit"/> picks, on a picture that <paramref name="answers"/> or not:
    /// the keyboard's pick, and a click's once there is a picture to click. Null where it says nothing.
    /// </summary>
    public static MapPick? Landing(bool answers, ExploreHit? hit) =>
        answers ? new MapPick(hit is { IsNode: true } node ? node.Node : null) : null;
}
