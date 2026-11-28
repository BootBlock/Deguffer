using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Deguffer.App.Views;

/// <summary>
/// The Explore page's choice of how to draw what was scanned. See the XAML for its layout.
///
/// <para>It says what the reader chose and draws what the page tells it to, and decides neither:
/// the page applies and writes each choice, and a preset can move the colouring from elsewhere.</para>
/// </summary>
public sealed partial class ExploreDrawingChoice : UserControl
{
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
        nameof(Options), typeof(ExploreDrawingOptions), typeof(ExploreDrawingChoice), new PropertyMetadata(null));

    private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);

    /// <summary>Each layout's icon, held while the switch is drawn without them.</summary>
    private readonly IconElement?[] _icons;

    /// <summary>How wide the switch is with its icons, as last measured with them.</summary>
    private double _withIcons;

    public ExploreDrawingChoice()
    {
        InitializeComponent();
        _icons = [.. ViewSelector.Items.Select(item => item.Icon)];
    }

    /// <summary>What is offered beside the switch. Set once, by the page, as it binds.</summary>
    public ExploreDrawingOptions? Options
    {
        get => (ExploreDrawingOptions?)GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    /// <summary>The reader chose a view in the switch.</summary>
    public event EventHandler<ExploreView>? ViewChosen;

    /// <summary>The reader chose a colouring in the Colour by box.</summary>
    public event EventHandler<ExploreColouring>? ColouringChosen;

    /// <summary>
    /// Select <paramref name="view"/>, one of <see cref="ExploreChoice.Views"/>, in the switch.
    ///
    /// <para>Safe to call from a handler of <see cref="ViewChosen"/>: the first call selects an item
    /// where none was and raises it once more, with the item now selected, and selecting the item
    /// already selected raises nothing.</para>
    /// </summary>
    public void Show(ExploreView view) => ViewSelector.SelectedItem = ViewSelector.Items[ExploreChoice.IndexOf(view)];

    /// <summary>Select <paramref name="colouring"/> in the Colour by box, on the terms of <see cref="Show(ExploreView)"/>.</summary>
    public void Show(ExploreColouring colouring) => ColourSelector.SelectedIndex = ExploreChoice.IndexOf(colouring);

    /// <summary>
    /// Draw the switch without its icons where its line is too narrow for them, and with them again
    /// where it is not. A switch cannot wrap, so one wider than its line cuts its last layout off,
    /// and the words name each layout where the icons only decorate them.
    ///
    /// <para>Measured unbounded first, because a measure within the line reports at most the line's
    /// width. The width with icons is kept while they are off, so the switch does not flip back and
    /// forth between two passes.</para>
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        ViewSelector.Measure(Unbounded);

        var iconsShown = ViewSelector.Items[0].Icon is not null;

        if (iconsShown)
        {
            _withIcons = ViewSelector.DesiredSize.Width;
        }

        if (_withIcons <= availableSize.Width != iconsShown)
        {
            for (var i = 0; i < _icons.Length; i++)
            {
                ViewSelector.Items[i].Icon = iconsShown ? null : _icons[i];
            }

            ViewSelector.Measure(Unbounded);
        }

        return base.MeasureOverride(availableSize);
    }

    /// <summary>
    /// Open the appearance window, or bring it forward where it is already open.
    ///
    /// <para>Its own window rather than a dialog on the page, because the reader has to see the map
    /// change as they choose: a dialog covers the page it belongs to. See
    /// <see cref="MapAppearanceWindow"/>.</para>
    /// </summary>
    private void OnAppearanceClicked(object sender, RoutedEventArgs e) => App.ShowMapAppearance();

    private void OnViewSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is { } item && sender.Items.IndexOf(item) is >= 0 and var index
            && index < ExploreChoice.Views.Count)
        {
            ViewChosen?.Invoke(this, ExploreChoice.Views[index]);
        }
    }

    private void OnColourSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ColourSelector.SelectedIndex is >= 0 and var index && index < ExploreChoice.Colourings.Count)
        {
            ColouringChosen?.Invoke(this, ExploreChoice.Colourings[index]);
        }
    }
}
