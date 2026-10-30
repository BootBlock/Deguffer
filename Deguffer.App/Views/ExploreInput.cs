using System.Windows.Input;
using Deguffer.App.Controls;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace Deguffer.App.Views;

/// <summary>
/// Turns what the reader does on the Explore page into the view-model's commands: the menu on a row
/// or a shape, the keys for the menu's actions, Backspace and the mouse's Back and Forward buttons,
/// and a double click on a row.
///
/// <para>§7.1 lets Explore act only on a selection the reader picked out by hand, so every route here
/// acts on the view-model's selection, and the two that start from the pointer move that selection
/// onto what the pointer is on first.</para>
/// </summary>
internal sealed class ExploreInput
{
    private readonly Page _page;
    private readonly ExploreViewModel _viewModel;
    private readonly ListView _rows;
    private readonly MenuFlyout _menu;

    /// <param name="menu">
    /// The one menu, opened by hand from the list and from the map. Two would be how the treemap comes
    /// to offer an action the list has stopped offering.
    /// </param>
    public ExploreInput(Page page, ExploreViewModel viewModel, ExploreMap map, ListView rows, MenuFlyout menu)
    {
        _page = page;
        _viewModel = viewModel;
        _rows = rows;
        _menu = menu;

        // The menu's actions on both surfaces that show a selection, rather than on the menu: a
        // flyout item's accelerator fires only while the flyout is open, which is exactly when the
        // keyboard is not what the reader is reaching for. The menu carries their text instead.
        Accelerate(map);
        Accelerate(rows);

        // Backspace goes back, as Alt+Left does. An accelerator rather than a KeyDown handler, because
        // a click on a map that cannot zoom focuses nothing, and a key then reaches no element to
        // bubble from: an accelerator is heard whatever holds the focus.
        Add(page, VirtualKey.Back, VirtualKeyModifiers.None, OnBackspaceInvoked);

        // Subscribed past the handled flag, because a ListViewItem marks the right-tap handled on its
        // way past, and an ordinary handler on the list never runs. Measured: an attached
        // ContextFlyout never appears either, and ContextRequested does not arrive at all. The map
        // needs none of this, because nothing between it and the pointer handles anything.
        rows.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler(OnRowsRightTapped), handledEventsToo: true);
        rows.DoubleTapped += OnRowsDoubleTapped;
        map.MenuRequested += (_, point) => Show(map, point);

        // The mouse's Back and Forward buttons from anywhere on the page, as a browser takes them.
        // Heard past the handled flag, because a row or a button under the pointer marks the press
        // handled on its way past.
        page.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPageReleased), handledEventsToo: true);
    }

    /// <summary>The menu's five actions as keys on <paramref name="surface"/>.</summary>
    private void Accelerate(UIElement surface)
    {
        var selection = _viewModel.Selection;

        Add(surface, VirtualKey.Delete, VirtualKeyModifiers.None, selection.DeleteCommand);
        Add(surface, VirtualKey.Delete, VirtualKeyModifiers.Shift, selection.DeletePermanentlyCommand);
        Add(surface, VirtualKey.Enter, VirtualKeyModifiers.None, selection.OpenCommand);
        Add(surface, VirtualKey.Enter, VirtualKeyModifiers.Menu, selection.PropertiesCommand);
        Add(surface, VirtualKey.Enter, VirtualKeyModifiers.Control, selection.RevealCommand);
    }

    /// <summary>
    /// A key on <paramref name="surface"/> that runs <paramref name="command"/>, and is handled only
    /// where the command ran, so a key that does not apply goes on to the control underneath.
    /// </summary>
    private static void Add(UIElement surface, VirtualKey key, VirtualKeyModifiers modifiers, ICommand command) =>
        Add(surface, key, modifiers, (_, args) => args.Handled = command.TryExecute());

    private static void Add(
        UIElement surface,
        VirtualKey key,
        VirtualKeyModifiers modifiers,
        TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> invoked)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += invoked;
        surface.KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>
    /// Backspace goes back, unless a box being typed in has the focus: there it deletes a character,
    /// and leaving the accelerator unhandled passes the key on to the box.
    /// </summary>
    private void OnBackspaceInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(_page.XamlRoot) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox)
        {
            return;
        }

        args.Handled = _viewModel.GoBackCommand.TryExecute();
    }

    /// <summary>
    /// The mouse's Back or Forward button. Taken as it comes up rather than as it goes down, as
    /// Windows takes it: the list is not then in the middle of a press, which it would read as the
    /// reader picking a row out of the rows the step is about to replace. See
    /// <see cref="Core.Viewing.ListGestures"/>.
    /// </summary>
    private void OnPageReleased(object sender, PointerRoutedEventArgs e)
    {
        var command = e.GetCurrentPoint(_page).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.XButton1Released => _viewModel.GoBackCommand,
            PointerUpdateKind.XButton2Released => _viewModel.GoForwardCommand,
            _ => null,
        };

        if (command is not null && command.TryExecute())
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Two clicks go in. A folder is descended into and a file is opened, which is what a double
    /// click means everywhere else the reader has done this.
    ///
    /// <para>Taken from the row the gesture landed on, not from the selection. The handler sits on
    /// the list, whose background is hit-testable, so a double-click on the empty space below the
    /// last row arrives here too, and reading the selection instead would open whatever was
    /// highlighted, from a gesture that landed on nothing.</para>
    /// </summary>
    private void OnRowsDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ListSelectionGuard.Row(e.OriginalSource) is not { Content: IExploreListed row })
        {
            return;
        }

        if (row.IsDirectory)
        {
            _viewModel.Descend(row.Node);
        }
        else
        {
            _viewModel.Selection.OpenCommand.TryExecute();
        }
    }

    /// <summary>
    /// Open the menu on the row the pointer landed on, moving the selection there first when that
    /// row is not already in it.
    ///
    /// <para>Moving the selection is the safety-relevant half. Without it the menu would act on
    /// whatever was highlighted before, so a right-click on one row could delete another, which is
    /// exactly the mistake <see cref="Core.Exploring.Acting.ExploreRemover"/> re-checks the policy to
    /// survive, and it should not be made here in the first place.</para>
    ///
    /// <para>Shown by hand rather than attached with <c>ContextFlyout</c>, which was measured: an
    /// attached flyout never appears, <c>ContextRequested</c> does not arrive at all, and a plain
    /// <c>RightTapped</c> handler does not run because a <c>ListViewItem</c> marks the gesture handled
    /// first. The keyboard reaches all five of these actions through the accelerators instead.</para>
    /// </summary>
    private void OnRowsRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ListSelectionGuard.Row(e.OriginalSource) is not { Content: IExploreListed row } container)
        {
            // The gesture landed on the list's own background. Clearing is what the map does on the
            // same miss, and the alternative is a menu positioned at the pointer whose Delete is live
            // against a row somewhere else on screen.
            _rows.SelectedItem = null;
        }
        else if (!_rows.SelectedItems.Contains(row))
        {
            _rows.SelectedItem = row;
            container.Focus(FocusState.Programmatic);
        }

        Show(_rows, e.GetPosition(_rows));
        e.Handled = true;
    }

    /// <summary>
    /// Open the menu on <paramref name="at"/>. The map has already reported what is under the pointer,
    /// so a menu there is about the shape the reader right-clicked rather than about whatever was
    /// picked before.
    /// </summary>
    /// <param name="point">
    /// Where, or null for a request from the keyboard, which has no position: placing the menu at the
    /// pointer's last location would put it wherever the mouse happens to be sitting.
    /// </param>
    private void Show(FrameworkElement at, Point? point) =>
        _menu.ShowAt(at, point is { } p ? new FlyoutShowOptions { Position = p } : new FlyoutShowOptions());
}
