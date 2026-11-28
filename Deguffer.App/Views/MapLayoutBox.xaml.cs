using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// A page's choice of layout for its map. See the XAML for why it is a box.
///
/// <para>It says what the reader chose and shows what the page tells it to, and decides neither: the
/// page applies each choice, and writes it where it is kept.</para>
/// </summary>
public sealed partial class MapLayoutBox : UserControl
{
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(MapLayoutBox), new PropertyMetadata(string.Empty));

    public MapLayoutBox() => InitializeComponent();

    /// <summary>What the layouts are for, said as the box's tooltip and help text.</summary>
    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>The reader chose a layout.</summary>
    public event EventHandler<ExploreView>? ViewChosen;

    /// <summary>
    /// Offer <paramref name="options"/>, once, as the page is built and before it shows a layout. In
    /// code rather than bound, because a binding is applied only as the page loads, and the page
    /// selects its stored layout before then.
    /// </summary>
    public void Offer(IReadOnlyList<MapLayoutOption> options) => LayoutSelector.ItemsSource = options;

    /// <summary>
    /// Select the layout at <paramref name="index"/> in the options offered, as the page's own Core
    /// list places it (<see cref="ExploreChoice.IndexOf(ExploreView)"/>, <see cref="MemoryChoice.IndexOf"/>).
    ///
    /// <para>Safe to call from a handler of <see cref="ViewChosen"/>: the first call moves the index
    /// off -1 and raises it once more, with the index now selected, and assigning an unchanged index
    /// raises nothing.</para>
    /// </summary>
    public void Show(int index) => LayoutSelector.SelectedIndex = index;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LayoutSelector.SelectedItem is MapLayoutOption chosen)
        {
            ViewChosen?.Invoke(this, chosen.View);
        }
    }
}
