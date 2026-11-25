using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Controls;

/// <summary>
/// A chevron that opens and closes the detail under a line, for a line that holds controls of its own.
///
/// <para>An <see cref="Expander"/> makes its whole header one toggle button, so a checkbox or a link
/// placed in the header is interactive content inside a button (WCAG 4.1.2): a click in the gaps
/// between them opens the detail, and a screen reader can report the header as one button holding
/// others. This is the header's chevron on its own, placed at the end of the line beside the line's
/// controls rather than around them. What it opens is bound to <see cref="IsExpanded"/> by whoever
/// draws it.</para>
///
/// <para>It is announced as an Expander's header is: a button that is expanded or collapsed, and that
/// UI Automation can expand and collapse, rather than a toggle that is on or off.</para>
/// </summary>
public sealed partial class DisclosureButton : Button
{
    /// <summary>The chevron's two ends, which are where an Expander's own chevron comes to rest.</summary>
    private static readonly string ClosedGlyph = char.ToString((char)0xE70D);

    private static readonly string OpenGlyph = char.ToString((char)0xE70E);

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded),
        typeof(bool),
        typeof(DisclosureButton),
        new PropertyMetadata(false, OnIsExpandedChanged));

    private readonly FontIcon _chevron = new()
    {
        Glyph = ClosedGlyph,
        FontSize = (double)Application.Current.Resources["GlyphSizeCaption"],
    };

    public DisclosureButton()
    {
        Content = _chevron;
        Click += (_, _) => IsExpanded = !IsExpanded;
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DisclosureButtonAutomationPeer(this);

    private static void OnIsExpandedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var button = (DisclosureButton)sender;
        var expanded = (bool)args.NewValue;

        button._chevron.Glyph = expanded ? OpenGlyph : ClosedGlyph;

        // Only a peer something has already asked for: creating one here would build automation
        // objects for every row of the list whether or not anything is listening.
        if (FrameworkElementAutomationPeer.FromElement(button) is DisclosureButtonAutomationPeer peer)
        {
            peer.RaisePropertyChangedEvent(
                ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                DisclosureButtonAutomationPeer.StateOf(!expanded),
                DisclosureButtonAutomationPeer.StateOf(expanded));
        }
    }

    private sealed partial class DisclosureButtonAutomationPeer(DisclosureButton owner)
        : ButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        public ExpandCollapseState ExpandCollapseState => StateOf(owner.IsExpanded);

        public static ExpandCollapseState StateOf(bool expanded) =>
            expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

        public void Expand() => owner.IsExpanded = true;

        public void Collapse() => owner.IsExpanded = false;

        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPatternCore(patternInterface);
    }
}
