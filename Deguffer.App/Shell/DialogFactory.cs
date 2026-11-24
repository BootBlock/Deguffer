using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Shell;

/// <summary>
/// The one place a <see cref="ContentDialog"/> is built, so none can open without what every dialog
/// needs. <c>DesignDialogTests</c> fails on one built anywhere else.
///
/// <para>Each needs three things a page cannot see go missing. A dialog built in code belongs to no
/// window, so without its owner's <see cref="XamlRoot"/> it has nowhere to open. It opens in the popup
/// layer rather than inside the page, so it does not inherit the theme applied to the window root, and
/// without the owner's theme it renders dark over a light window. And its default button is Close, so
/// Enter never confirms something the user has not read. Every dialog is shown through
/// <see cref="ModalDialog"/>, which refuses a second one.</para>
///
/// <para>No <c>Style</c> is set. WinUI's own samples set <c>DefaultContentDialogStyle</c> on a dialog
/// built in code, a habit from WinUI 2, where the implicit style was the old one. WinUI 3's
/// <c>generic.xaml</c> declares the implicit <c>ContentDialog</c> style as based on
/// <c>DefaultContentDialogStyle</c>, so a dialog built here already wears it, as the XAML-declared
/// <c>NetworkBrowserDialog</c> does.</para>
/// </summary>
internal static class DialogFactory
{
    /// <summary>What the button that dismisses a confirmation says, whatever it was about.</summary>
    internal const string Cancel = "Cancel";

    /// <summary>
    /// A question with a button that confirms it and one that dismisses it, the dismissal being the
    /// default.
    /// </summary>
    /// <param name="owner">The element the dialog opens over, whose root and theme it takes.</param>
    /// <param name="dismissLabel">
    /// The dismissing button, for a question whose answers are two courses rather than yes and no.
    /// </param>
    internal static ContentDialog Question(
        FrameworkElement owner,
        string title,
        object content,
        string confirmLabel,
        string dismissLabel = Cancel)
    {
        var dialog = Build(owner, title, dismissLabel);
        dialog.Content = content;
        dialog.PrimaryButtonText = confirmLabel;
        return dialog;
    }

    /// <summary>A dialog that only tells, with one button that closes it.</summary>
    internal static ContentDialog Statement(FrameworkElement owner, string title, string closeLabel) =>
        Build(owner, title, closeLabel);

    /// <summary>
    /// Give a dialog declared in XAML the root and the theme a dialog built here gets. Its buttons are
    /// its markup's.
    /// </summary>
    internal static T Adopt<T>(FrameworkElement owner, T dialog)
        where T : ContentDialog
    {
        dialog.XamlRoot = owner.XamlRoot;
        dialog.RequestedTheme = owner.ActualTheme;
        return dialog;
    }

    /// <summary>One paragraph of a dialog's body, wrapped at word boundaries.</summary>
    internal static TextBlock Paragraph(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.WrapWholeWords };

    private static ContentDialog Build(FrameworkElement owner, string title, string closeLabel) =>
        Adopt(owner, new ContentDialog
        {
            Title = title,
            CloseButtonText = closeLabel,
            DefaultButton = ContentDialogButton.Close,
        });
}
