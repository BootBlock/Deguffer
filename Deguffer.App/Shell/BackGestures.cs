using System.Windows.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Deguffer.App.Shell;

/// <summary>
/// The ways Windows says "back", all running the window's one Back command: the rail's back button,
/// Alt+Left, the keyboard's own Back key, and the mouse's Back button. Each is offered only while the
/// command can run.
///
/// <para>The keys are switched off rather than left unhandled while there is nowhere to go back to.
/// Measured: an enabled accelerator on the window's content takes Alt+Left from the Explore page's
/// own Back button even when its handler leaves the key unhandled, so Explore stopped stepping back.
/// A disabled one is passed over. While a link has been followed the window's keys come first, so a
/// link that opens a page with a Back of its own (Explore's) has to decide which Back the keys mean
/// there before it lands.</para>
///
/// <para>The mouse's button is heard as it bubbles, so the Explore page's handler, which marks it
/// handled where Explore stepped back, comes first at any time.</para>
/// </summary>
internal static class BackGestures
{
    /// <param name="root">The window's content, which every page and the rail are inside.</param>
    /// <param name="rail">The navigation view whose back button is shown while <paramref name="back"/> can run.</param>
    public static void Answer(UIElement root, NavigationView rail, ICommand back)
    {
        KeyboardAccelerator[] keys =
        [
            Key(VirtualKey.Left, VirtualKeyModifiers.Menu, back),
            Key(VirtualKey.GoBack, VirtualKeyModifiers.None, back),
        ];

        foreach (var key in keys)
        {
            root.KeyboardAccelerators.Add(key);
        }

        Offer(rail, keys, back);
        back.CanExecuteChanged += (_, _) => Offer(rail, keys, back);
        rail.BackRequested += (_, _) => back.TryExecute();

        // Taken as it comes up rather than as it goes down, as Windows and the Explore page take it.
        root.PointerReleased += (_, e) =>
        {
            if (e.GetCurrentPoint(root).Properties.PointerUpdateKind == PointerUpdateKind.XButton1Released)
            {
                e.Handled = back.TryExecute();
            }
        };
    }

    /// <summary>
    /// The rail's back button and the keys only while Back leads somewhere. A disabled button shown on
    /// every page would be a control that never works for a reader who never follows a link.
    /// </summary>
    private static void Offer(NavigationView rail, KeyboardAccelerator[] keys, ICommand back)
    {
        var can = back.CanExecute(null);
        rail.IsBackEnabled = can;
        rail.IsBackButtonVisible = can ? NavigationViewBackButtonVisible.Visible : NavigationViewBackButtonVisible.Collapsed;

        foreach (var key in keys)
        {
            key.IsEnabled = can;
        }
    }

    private static KeyboardAccelerator Key(VirtualKey key, VirtualKeyModifiers modifiers, ICommand back)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) => args.Handled = back.TryExecute();
        return accelerator;
    }
}
