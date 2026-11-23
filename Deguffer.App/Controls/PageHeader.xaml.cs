using Deguffer.App.Shell;
using Microsoft.UI.Xaml;

namespace Deguffer.App.Controls;

/// <summary>
/// A page's header, named from its <see cref="Destination"/> so it reads the name the rail and the
/// window title read. <see cref="Actions"/> is the slot at its right.
/// </summary>
public sealed partial class PageHeader
{
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions),
        typeof(UIElement),
        typeof(PageHeader),
        new PropertyMetadata(null));

    public PageHeader() => InitializeComponent();

    /// <summary>The page this header names. Set once, in the page's markup.</summary>
    public Destination Destination { get; set; }

    /// <summary>The page's name, read once as the header is built.</summary>
    public string Title => DestinationNames.Of(Destination);

    /// <summary>What governs the page as a whole, set at the header's right.</summary>
    public UIElement? Actions
    {
        get => (UIElement?)GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }
}
