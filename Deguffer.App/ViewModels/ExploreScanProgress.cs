using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// How far through the Explore page's scan is: the bar over the card, and the line under the map that
/// counts what the scan has read.
///
/// <para>Separate from <see cref="ExploreViewModel"/> because it reads neither the tree on screen nor
/// the node being looked at. The page hands it each report the scan makes, and the snapshot a report
/// carries stays the page's to show.</para>
/// </summary>
public sealed partial class ExploreScanProgress(StatusLineViewModel status) : ObservableObject
{
    /// <summary>
    /// How far through, 0 to 1, or null where the route cannot say.
    ///
    /// <para>The file table states its record count before the first read, so that route drives a
    /// real bar. A walk cannot know how many directories it has yet to open, so it gets an
    /// indeterminate one rather than a made-up denominator that would run to 90% and stop.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value))]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    public partial double? Fraction { get; private set; }

    /// <summary>
    /// The same figure as a plain number, because a progress bar's value is not nullable and a
    /// binding that has to fall back is a binding that fails silently when it is wrong.
    /// </summary>
    public double Value => Fraction ?? 0;

    /// <summary>Whether the bar has to be indeterminate. See <see cref="Fraction"/>.</summary>
    public bool IsIndeterminate => Fraction is null;

    /// <summary>Show how far the scan has got, on the bar and on the line under the map.</summary>
    public void Report(ExploreProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        Fraction = progress.Fraction;

        status.Say(StatusLine.Said(progress.Total is null
            ? $"Scanning… {progress.Done:N0} items, {FreeSpace.Format(progress.BytesSeen)} so far"
            : $"Reading the file table… {progress.Fraction:P0}"));
    }

    /// <summary>Back to knowing nothing, as a scan starts and once it ends.</summary>
    public void Reset() => Fraction = null;
}
