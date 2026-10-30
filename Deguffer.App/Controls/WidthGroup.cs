using Microsoft.UI.Xaml;

namespace Deguffer.App.Controls;

/// <summary>
/// Elements that all take the width of the widest of them, for a column that has to line up across
/// rows that each build their own <c>Grid</c>. WinUI 3 has no <c>SharedSizeGroup</c>.
///
/// <para>A fixed column width cannot do it, because the column holds text the app does not control:
/// an estimate reads "about 1.0 GB" and a ceiling "up to 99.9 GB", and Windows text scaling widens
/// every figure. A column too narrow for its text cuts the figure off, which is the one thing on the
/// row that must be read whole.</para>
///
/// <para>A member is placed in an <c>Auto</c> column, so it is measured at its own width. When that
/// is wider than the group's, the group widens, and every member present takes the new width as its
/// <see cref="FrameworkElement.MinWidth"/>, a member arriving later included. The group never
/// narrows while the page lives: finding a narrower widest member would mean measuring every member
/// again without its minimum, and a column left a little wider than it needs is harmless.</para>
/// </summary>
public sealed partial class WidthGroup
{
    public static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached(
        "Group", typeof(WidthGroup), typeof(WidthGroup), new PropertyMetadata(null, OnGroupChanged));

    private readonly HashSet<FrameworkElement> _members = [];

    private double _width;

    public static WidthGroup? GetGroup(FrameworkElement element) => (WidthGroup?)element.GetValue(GroupProperty);

    public static void SetGroup(FrameworkElement element, WidthGroup? value) => element.SetValue(GroupProperty, value);

    private static void OnGroupChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        // Set once from markup on an element that is not yet loaded, which is the only use there is.
        if (element is FrameworkElement member && args.NewValue is WidthGroup group)
        {
            member.Loaded += group.OnLoaded;
            member.Unloaded += group.OnUnloaded;
            member.SizeChanged += group.OnSizeChanged;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        var member = (FrameworkElement)sender;
        _members.Add(member);
        member.MinWidth = Math.Max(member.MinWidth, _width);
    }

    // A recycled row's container unloads and loads again, so a member is held only while it is shown.
    // An element moved to a new parent can be loaded there before it is unloaded from the old one,
    // so one that is still loaded stays a member.
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        var member = (FrameworkElement)sender;

        if (!member.IsLoaded)
        {
            _members.Remove(member);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.NewSize.Width <= _width)
        {
            return;
        }

        _width = args.NewSize.Width;

        foreach (var member in _members)
        {
            member.MinWidth = _width;
        }
    }
}
