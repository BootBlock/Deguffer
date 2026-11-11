using System.ComponentModel;
using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Deguffer.App.Views;

/// <summary>
/// The network browser as a dialog: the tree, the box a computer is added from, the filter and the
/// path bar, wired to <see cref="NetworkBrowser"/>. It decides nothing.
///
/// <para>The tree's selection is written both ways by hand rather than bound: TreeView's
/// <c>SelectedItem</c> is typed as an object, and the browser selects a node itself as it opens at
/// the place it was left.</para>
/// </summary>
public sealed partial class NetworkBrowserDialog : ContentDialog
{
    /// <summary>True while the selection is written from the browser, so the tree's echo is not taken for the reader's choice.</summary>
    private bool _selecting;

    /// <param name="browser">The browser, which the caller disposes once the dialog has closed.</param>
    public NetworkBrowserDialog(NetworkBrowser browser)
    {
        Browser = browser;
        InitializeComponent();
        Browser.PropertyChanged += OnBrowserChanged;
    }

    public NetworkBrowser Browser { get; }

    /// <summary>The folder chosen, or null where the dialog closed without one.</summary>
    public string? Chosen { get; private set; }

    /// <summary>Open where the browser was last left, once the tree's containers exist to be opened.</summary>
    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        Tree.Focus(FocusState.Programmatic);
        await Browser.ShownAsync();
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (args.Result is ContentDialogResult.Primary)
        {
            Chosen = Browser.Choice;
        }

        Browser.PropertyChanged -= OnBrowserChanged;
        Browser.Closing();
    }

    /// <summary>Use the selected folder and close, which is what the primary button does.</summary>
    private void Choose()
    {
        if (Browser.Choice is { } choice)
        {
            Chosen = choice;
            Hide();
        }
    }

    private void OnTreeSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (!_selecting)
        {
            Browser.Selected = sender.SelectedItem as NetworkNodeRow;
        }
    }

    private void OnBrowserChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(NetworkBrowser.Selected) || ReferenceEquals(Tree.SelectedItem, Browser.Selected))
        {
            return;
        }

        _selecting = true;

        try
        {
            Tree.SelectedItem = Browser.Selected;
        }
        finally
        {
            _selecting = false;
        }
    }

    /// <summary>Double-clicking a share or folder uses it. A computer or a heading is opened instead, since neither is a folder.</summary>
    private void OnTreeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Browser.CanUse)
        {
            e.Handled = true;
            Choose();
        }
        else if (Browser.Selected is { } node)
        {
            e.Handled = true;
            node.IsExpanded = !node.IsExpanded;
        }
    }

    /// <summary>Enter uses the selected folder. The arrow keys move and open nodes as the tree already does.</summary>
    private void OnTreePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter && Browser.CanUse)
        {
            e.Handled = true;
            Choose();
        }
    }

    /// <summary>F5 lists the selected node again, or the whole tree where nothing is selected.</summary>
    private void OnRefreshInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Browser.RefreshCommand.Execute(Browser.Selected);
    }

    /// <summary>Ctrl+L goes to the box a computer is added from, as it goes to the address bar elsewhere in Windows.</summary>
    private void OnAddInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ComputerBox.Focus(FocusState.Keyboard);
        ComputerBox.SelectAll();
    }

    /// <summary>Enter in the computer box adds it, rather than using the selected folder, which the dialog's default button would.</summary>
    private void OnComputerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter)
        {
            e.Handled = true;

            if (Browser.AddComputerCommand.CanExecute(null))
            {
                Browser.AddComputerCommand.Execute(null);
            }
        }
    }

    private void OnSignIn(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NetworkNodeRow node })
        {
            Browser.SignInCommand.Execute(node);
        }
    }

    private void OnRemoveComputer(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NetworkNodeRow node })
        {
            Browser.RemoveComputerCommand.Execute(node);
        }
    }
}
