using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// A list of §5.6 checks, each by its subject, what it found and why it was made. See the XAML for
/// why every page that acts shares it.
/// </summary>
public sealed partial class VerificationCheckList : UserControl
{
    public static readonly DependencyProperty ChecksProperty =
        DependencyProperty.Register(nameof(Checks), typeof(object), typeof(VerificationCheckList), new PropertyMetadata(null));

    public VerificationCheckList() => InitializeComponent();

    /// <summary>The checks to list, as a collection the page keeps up to date in place.</summary>
    public object? Checks
    {
        get => GetValue(ChecksProperty);
        set => SetValue(ChecksProperty, value);
    }
}
