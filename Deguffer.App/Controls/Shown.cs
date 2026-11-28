using Microsoft.UI.Xaml;

namespace Deguffer.App.Controls;

/// <summary>
/// A visibility from a fact, for an <c>x:Bind</c> that has to show an element while the fact is
/// false. A bound <c>bool</c> converts to a visibility by itself only the other way round.
/// </summary>
public static class Shown
{
    /// <summary>Collapsed while <paramref name="fact"/> holds, and visible otherwise.</summary>
    public static Visibility Unless(bool fact) => fact ? Visibility.Collapsed : Visibility.Visible;
}
