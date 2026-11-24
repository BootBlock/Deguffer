using Deguffer.App.Shell;

namespace Deguffer.App.Controls;

/// <summary>
/// A page's introduction: <see cref="Text"/> in view, <see cref="Details"/> behind a disclosure.
/// Both are set once, in the page's markup, where <c>DesignSettingsTextTests</c> holds the sentence
/// in view to the budget a settings row's description has.
/// </summary>
public sealed partial class PageIntroduction
{
    public PageIntroduction() => InitializeComponent();

    /// <summary>The page this introduces, which names its disclosure to a screen reader.</summary>
    public Destination Destination { get; set; }

    /// <summary>The one sentence that says what the page is for.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The rest of the explanation.</summary>
    public string Details { get; set; } = string.Empty;

    private string Subject => DestinationNames.Of(Destination);
}
