using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Controls;

/// <summary>
/// A split button that is drawn disabled as soon as it is disabled. WinUI's own reads
/// <c>IsEnabled</c> only when a pointer, its flyout or loading next makes it choose a visual state,
/// so one disabled while nothing touches it stays in Normal: its halves keep their fill, and their
/// disabled text is drawn on it. Under the accent brushes that text cannot be read.
/// </summary>
public sealed partial class EnabledAwareSplitButton : SplitButton
{
    public EnabledAwareSplitButton()
    {
        // Normal on the way back, because the control has no public way to choose its own state.
        // The next pointer or flyout event replaces it with the exact one, as it would anyway.
        IsEnabledChanged += (_, e) =>
            VisualStateManager.GoToState(this, (bool)e.NewValue ? "Normal" : "Disabled", useTransitions: true);
    }
}
