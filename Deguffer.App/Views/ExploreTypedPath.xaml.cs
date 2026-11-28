using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Exploring.Network;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Deguffer.App.Views;

/// <summary>The Explore page's Path… button and its box. See the XAML for why it is a control of its own.</summary>
public sealed partial class ExploreTypedPath : UserControl
{
    public static readonly DependencyProperty TypedProperty = DependencyProperty.Register(
        nameof(Typed),
        typeof(ExploreTypedFolder),
        typeof(ExploreTypedPath),
        new PropertyMetadata(null, (path, e) =>
        {
            // The page points at what was typed, so the box has done its work and closes.
            if (e.NewValue is ExploreTypedFolder typed)
            {
                typed.Accepted += ((ExploreTypedPath)path).OnTypedAccepted;
            }
        }));

    public ExploreTypedPath() => InitializeComponent();

    /// <summary>What has been typed and what is done with it. Set once, by the page, as it binds.</summary>
    public ExploreTypedFolder? Typed
    {
        get => (ExploreTypedFolder?)GetValue(TypedProperty);
        set => SetValue(TypedProperty, value);
    }

    /// <summary>
    /// <see cref="Typed"/>, which the page binds before the box can be opened, so no handler here
    /// can run without it.
    /// </summary>
    private ExploreTypedFolder Folder =>
        Typed ?? throw new InvalidOperationException("The page sets what was typed as it binds, before the box can open.");

    /// <summary>
    /// The box opening starts its suggestion session, so what the network answers is kept until it
    /// closes. The list itself opens once the box has focus (<see cref="OnTypedFocused"/>).
    /// </summary>
    private void OnTypedOpened(object? sender, object e)
    {
        Folder.Suggestions.Open();
        TypedBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// A check still asking a server when the box closes is stopped, so a server that answers late
    /// cannot point the page at a path the reader has walked away from. The suggestion session ends
    /// with it, and so does any lookup it was waiting on.
    /// </summary>
    private void OnTypedClosed(object? sender, object e)
    {
        if (Folder.GoCommand.IsRunning)
        {
            Folder.GoCancelCommand.Execute(null);
        }

        Folder.Suggestions.Close();
        _highlighted = null;
    }

    /// <summary>The suggestion the arrow keys last highlighted, which Tab completes. Null once the text is typed over.</summary>
    private PathSuggestionRow? _highlighted;

    /// <summary>The box gaining focus lists what fits what it already holds: everything this account uses, while it is empty.</summary>
    private async void OnTypedFocused(object sender, RoutedEventArgs e)
    {
        TypedBox.IsSuggestionListOpen = true;
        await Folder.Suggestions.SuggestAsync(TypedBox.Text);
    }

    /// <summary>
    /// Typing lists what fits. Only typing: the box also raises this as a highlighted suggestion
    /// writes its path into the text, and listing again then would replace the list being moved through.
    /// </summary>
    private async void OnTypedTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason is not AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _highlighted = null;
        await Folder.Suggestions.SuggestAsync(sender.Text);
    }

    private void OnTypedSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args) =>
        _highlighted = args.SelectedItem as PathSuggestionRow;

    /// <summary>
    /// Tab completes the highlighted suggestion and lists the next level: a computer's shares, or a
    /// share's folders. Without one highlighted, Tab moves on as it does anywhere else.
    /// </summary>
    private async void OnTypedPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not Windows.System.VirtualKey.Tab || _highlighted is not { } highlighted)
        {
            return;
        }

        e.Handled = true;
        await CompleteAsync(highlighted);
    }

    /// <summary>
    /// Enter, or a suggestion clicked. A folder chosen fills the box and is used, as Enter on typed
    /// text is. A computer is no folder (<see cref="NetworkPlace.IsFolder"/>),
    /// so choosing one completes it and lists its shares instead of being refused.
    /// </summary>
    private async void OnTypedSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var typed = Folder;

        if (args.ChosenSuggestion is not PathSuggestionRow chosen)
        {
            await typed.UseAsync(typed.Text);
        }
        else if (chosen.Suggestion.Place.IsFolder)
        {
            await typed.UseAsync(chosen.Path);
        }
        else
        {
            await CompleteAsync(chosen);
        }
    }

    /// <summary>Write <paramref name="suggestion"/>'s next stage into the box and list what is inside it.</summary>
    private async Task CompleteAsync(PathSuggestionRow suggestion)
    {
        _highlighted = null;
        Folder.Text = suggestion.Suggestion.Completion;
        TypedBox.IsSuggestionListOpen = true;
        await Folder.Suggestions.SuggestAsync(suggestion.Suggestion.Completion);
    }

    /// <summary>
    /// The chevron: a tree of the network for a share whose name is not known. The box closes while
    /// the dialog is open, because WinUI shows one popup layer at a time, and opens again with the
    /// place chosen in it, checked as typed text is, so a share that is not there says so in the box.
    /// </summary>
    private async void OnBrowseNetwork(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not { } window)
        {
            return;
        }

        TypedFlyout.Hide();

        using var browser = new NetworkBrowser(
            NetworkAccess.Machine, TimeProvider.System, App.Preferences, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var dialog = DialogFactory.Adopt(this, new NetworkBrowserDialog(browser));

        await ModalDialog.ShowAsync(dialog);

        if (dialog.Chosen is not { } chosen)
        {
            return;
        }

        TypedFlyout.ShowAt(TypedButton);
        await Folder.UseAsync(chosen);
    }

    private void OnTypedAccepted(object? sender, EventArgs e) => TypedFlyout.Hide();
}
