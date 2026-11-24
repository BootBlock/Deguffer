using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;

namespace Deguffer.App.Controls;

/// <summary>
/// A Storage row's basis and tier chips, drawn once for both of the row's views so the two cannot
/// drift apart.
/// </summary>
public sealed partial class ProviderChips
{
    public static readonly DependencyProperty FactsProperty = DependencyProperty.Register(
        nameof(Facts),
        typeof(ProviderFacts),
        typeof(ProviderChips),
        new PropertyMetadata(null));

    public ProviderChips() => InitializeComponent();

    /// <summary>
    /// The provider the chips describe. A dependency property because a list recycles its row
    /// containers, which hands one set of chips to another row.
    /// </summary>
    public ProviderFacts? Facts
    {
        get => (ProviderFacts?)GetValue(FactsProperty);
        set => SetValue(FactsProperty, value);
    }
}
