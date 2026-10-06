using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Scanning;
using Deguffer.Core.Viewing;
using Windows.UI;

namespace Deguffer.App.ViewModels;

/// <summary>One kind of file in the breakdown of the folder on screen, worded for the panel.</summary>
/// <param name="Swatch">The colour a map coloured by type paints the kind, beside its name.</param>
/// <param name="Share">How much of the folder the kind is, 0 to 100.</param>
/// <param name="Extensions">The largest extensions of the kind with their sizes, or empty.</param>
public sealed record ExploreTypeRow(
    FileCategory Category, string Label, Color Swatch, string Size, double Share, string Files, string Extensions)
{
    /// <summary>What a screen reader says for the row, which names everything the colour does not.</summary>
    public string Description => Extensions.Length > 0
        ? $"{Label}: {Size} in {Files}. Largest: {Extensions}"
        : $"{Label}: {Size} in {Files}";

    /// <summary>The row <paramref name="share"/> of a folder of <paramref name="total"/> bytes is.</summary>
    public static ExploreTypeRow For(TypeShare share, long total) => new(
        share.Category,
        FileCategories.Label(share.Category),
        ColourOf(TypePalette.For(share.Category)),
        FreeSpace.Format(share.Bytes),
        total > 0 ? 100.0 * share.Bytes / total : 0,
        share.Files == 1 ? "1 file" : $"{share.Files:N0} files",
        string.Join(", ", share.Extensions.Select(extension =>
            $"{(extension.Extension.Length > 0 ? extension.Extension : "no extension")} {FreeSpace.Format(extension.Bytes)}")));

    private static Color ColourOf(TileColour colour) => Color.FromArgb(255, colour.Red, colour.Green, colour.Blue);
}

/// <summary>
/// The Explore page's answer to "what kind of file fills this folder": the breakdown of the folder on
/// screen by kind, and the kind of file every node holds most of, which the map is coloured by.
///
/// <para>Separate from <see cref="ExploreViewModel"/>, as <see cref="ExploreGrowth"/> and
/// <see cref="ExploreFiles"/> are, because it holds state of its own that the page only reads. The
/// decisions in it are Core's: what each kind is (<see cref="FileCategories"/>), what a folder holds
/// (<see cref="TypeBreakdown"/>) and which kind it is painted (<see cref="DominantTypes"/>). This
/// decides only when to ask.</para>
///
/// <para>§7.1: a kind says what the files are and never that they can go. Nothing here is offered or
/// selected, and choosing a kind only lists its files.</para>
/// </summary>
public sealed partial class ExploreTypes : ObservableObject
{
    private ExploreTree? _tree;
    private int _root;

    /// <summary>The breakdown <see cref="Rows"/> shows, so a redraw of the same folder asks nothing again.</summary>
    private TypeBreakdown? _breakdown;

    /// <summary>The breakdown in flight, cancelled when another replaces it.</summary>
    private CancellationTokenSource? _breaking;

    /// <summary>The measurement of the tree's kinds in flight, cancelled when another tree replaces it.</summary>
    private CancellationTokenSource? _measuring;

    /// <summary>The tree <see cref="_measuring"/> is measuring.</summary>
    private ExploreTree? _measuringTree;

    /// <summary>
    /// The kinds of file, largest first, in the folder on screen. Updated in place rather than rebuilt,
    /// so a snapshot of a running scan moves a row rather than rebuilding the list (G4).
    /// </summary>
    public ObservableCollection<ExploreTypeRow> Rows { get; } = [];

    /// <summary>How much the folder holds and in how many files, or empty until that is measured.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>
    /// Whether the map is coloured by type, which is when the panel is on screen. Nothing is measured
    /// while it is not: each measurement is a pass over the folder or the tree, and a walked scan
    /// publishes a tree every few hundred milliseconds.
    /// </summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>
    /// The kind of file every node of the tree on screen holds most of, or null until that has been
    /// measured. Measured only for a finished scan: a running one publishes a new tree every few
    /// hundred milliseconds, and its folders would be painted as not measured between each, so they
    /// stay that way until it finishes and its files are painted their own kind meanwhile.
    /// </summary>
    public DominantTypes? Dominant { get; private set; }

    /// <summary>Raised when <see cref="Dominant"/> arrives, so the map is drawn again.</summary>
    public event EventHandler? Changed;

    /// <summary>Point the panel at <paramref name="root"/> of <paramref name="tree"/>, or at nothing.</summary>
    public void Show(ExploreTree? tree, int root)
    {
        if (!ReferenceEquals(tree, _tree))
        {
            // Both hold the tree they describe, and a tree is a volume's worth of arrays.
            Dominant = null;
            _breakdown = null;

            if (_measuringTree is not null)
            {
                Cancel(ref _measuring);
                _measuringTree = null;
            }
        }

        if (!ReferenceEquals(tree, _tree) || root != _root)
        {
            Cancel(ref _breaking);
        }

        _tree = tree;
        _root = root;

        Measure();
    }

    partial void OnIsActiveChanged(bool value) => Measure();

    /// <summary>Ask for whatever the tree and folder on screen are missing. Fire and forget, as <see cref="ExploreFiles"/> does.</summary>
    private void Measure()
    {
        _ = BreakDownAsync();
        _ = MeasureKindsAsync();
    }

    private async Task BreakDownAsync()
    {
        if (_tree is not { } tree)
        {
            Cancel(ref _breaking);
            LiveList.Show(Rows, [], row => row.Category);
            Summary = string.Empty;
            return;
        }

        if (!IsActive || (_breakdown is { } shown && ReferenceEquals(shown.Tree, tree) && shown.Root == _root))
        {
            return;
        }

        var root = _root;
        var breaking = Replace(ref _breaking);

        try
        {
            var breakdown = await Task.Run(() => TypeBreakdown.Measure(tree, root, breaking.Token), breaking.Token);

            // A breakdown of another folder or tree is about a question nobody is asking any more.
            if (!ReferenceEquals(tree, _tree) || root != _root)
            {
                return;
            }

            _breakdown = breakdown;

            var total = tree.SizeOf(root);
            var files = breakdown.Shares.Sum(share => share.Files);

            LiveList.Show(Rows, [.. breakdown.Shares.Select(share => ExploreTypeRow.For(share, total))], row => row.Category);

            Summary = files switch
            {
                0 => "There are no files here.",
                1 => $"{FreeSpace.Format(total)} in 1 file.",
                _ => $"{FreeSpace.Format(total)} in {files:N0} files.",
            };
        }
        catch (OperationCanceledException)
        {
            // Another folder or tree replaced this one, and its breakdown is the one that will be shown.
        }
    }

    private async Task MeasureKindsAsync()
    {
        // A tree already measured, or being measured, needs nothing more for a move to another of its
        // folders.
        if (!IsActive || _tree is not { ChildOrder: ExploreChildOrder.BySize } tree
            || ReferenceEquals(Dominant?.Tree, tree) || ReferenceEquals(_measuringTree, tree))
        {
            return;
        }

        var measuring = Replace(ref _measuring);
        _measuringTree = tree;

        try
        {
            var measured = await Task.Run(() => DominantTypes.Measure(tree, measuring.Token), measuring.Token);

            if (ReferenceEquals(tree, _tree))
            {
                Dominant = measured;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // Another tree replaced this one.
        }
        finally
        {
            if (ReferenceEquals(_measuring, measuring))
            {
                _measuring = null;
                _measuringTree = null;
            }
        }
    }

    /// <summary>Cancel the work in flight in <paramref name="slot"/>, and put a new token in its place.</summary>
    private static CancellationTokenSource Replace(ref CancellationTokenSource? slot)
    {
        Cancel(ref slot);
        slot = new CancellationTokenSource();
        return slot;
    }

    private static void Cancel(ref CancellationTokenSource? slot)
    {
        slot?.Cancel();
        slot = null;
    }
}
