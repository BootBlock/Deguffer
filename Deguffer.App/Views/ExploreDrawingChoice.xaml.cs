using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

    public ExploreDrawingChoice()
    {
        InitializeComponent();

        LayoutBox.Offer(MapLayoutOption.Explore);
        LayoutBox.ViewChosen += (_, view) => ViewChosen?.Invoke(this, view);
    }

    /// <summary>What is offered beside the Layout box. Set once, by the page, as it binds.</summary>
    public ExploreDrawingOptions? Options
    {
        get => (ExploreDrawingOptions?)GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    /// <summary>The reader chose a view in the Layout box.</summary>
    public event EventHandler<ExploreView>? ViewChosen;

    /// <summary>The reader chose a colouring in the Colour by box.</summary>
    public event EventHandler<ExploreColouring>? ColouringChosen;

    /// <summary>
    /// Select <paramref name="view"/>, one of <see cref="ExploreChoice.Views"/>, in the Layout box, on
    /// the terms of <see cref="MapLayoutBox.Show"/>.
    /// </summary>
    public void Show(ExploreView view) => LayoutBox.Show(view);

    /// <summary>Select <paramref name="colouring"/> in the Colour by box, on the terms of <see cref="Show(ExploreView)"/>.</summary>
    public void Show(ExploreColouring colouring) => ColourSelector.SelectedIndex = ExploreChoice.IndexOf(colouring);

    /// <summary>
    /// Open the appearance window, or bring it forward where it is already open.
    ///
    /// <para>Its own window rather than a dialog on the page, because the reader has to see the map
    /// change as they choose: a dialog covers the page it belongs to. See
    /// <see cref="MapAppearanceWindow"/>.</para>
    /// </summary>
    private void OnAppearanceClicked(object sender, RoutedEventArgs e) => App.ShowMapAppearance();

    private void OnColourSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ColourSelector.SelectedIndex is >= 0 and var index && index < ExploreChoice.Colourings.Count)
        {
            ColouringChosen?.Invoke(this, ExploreChoice.Colourings[index]);
        }
    }
}
