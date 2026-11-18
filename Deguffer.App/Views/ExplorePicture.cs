using Deguffer.App.Controls;
using Deguffer.App.ViewModels;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.App.Views;

/// <summary>
/// Which picture the Explore page's map draws: the last kept scan of the drive being scanned, a step
/// of the replay of what grew, or the tree on screen, with the colours, the labels and the volume each
/// is drawn with.
///
/// <para>Apart from the page because the choice is one thing, made from the view-model alone, and the
/// page only says when to make it. Which of the three is on screen is the view-model's to decide
/// (<see cref="ExploreViewModel.KeptScan"/>, <see cref="ExploreReplay.Frame"/>).</para>
/// </summary>
internal static class ExplorePicture
{
    /// <summary>Hand <paramref name="map"/> the picture <paramref name="page"/> shows now.</summary>
    public static void Draw(ExploreMap map, ExploreViewModel page)
    {
        // The last kept scan of the drive being scanned, until the scan has a tree of its own (#491):
        // the whole drive as that scan found it, with the volume beside it as it was then.
        if (page.KeptScan.Tree is { } kept)
        {
            var colours = ShapeColours.ForKept(page.SelectedColouring, page.SelectedScheme);

            map.Show(kept, kept.RootNode, page.SelectedView, _ => colours, kept.Caption, page.Look, kept.Volume);
            return;
        }

        // A step of the replay of what grew, in place of the tree now: the same folder, as the map
        // coloured by growth paints it, with the volume beside it as it was at that step (#285).
        if (page.Replay.Frame is { } frame)
        {
            map.Show(
                frame,
                page.CurrentNode,
                page.SelectedView,
                _ => ShapeColours.ForReplay(frame),
                frame.Caption,
                page.Look,
                frame.Replay.VolumeAt(frame.Step, page.VolumeBeside));

            return;
        }

        map.Show(
            page.Tree,
            page.CurrentNode,
            page.SelectedView,
            page.SelectedColouring,
            page.SelectedScheme,
            page.Look,
            page.VolumeBeside,
            page.Growth.Comparison,
            page.Types.Dominant,
            page.Visiting.Revisiting);
    }
}
