using Deguffer.Core.Configuration;
using Deguffer.Core.Viewing;

namespace Deguffer.App.Shell;

/// <summary>
/// Opens and folds the window's navigation rail as the window's width and the reader's toggle say,
/// and remembers what the toggle chose. <see cref="RailFold"/> makes both decisions.
///
/// <para>The rail reports every change to it, its own folds included, and only the toggle's are a
/// choice. A fold of its own read back as one would rewrite the setting each time the window crossed
/// the width: a reader who opened the rail on a narrow window would find it handed back to the width
/// the first time the window was wide. So <see cref="Fit"/> notes what it shows before it shows it,
/// and a report of the rail as it was last shown is not a choice.</para>
///
/// <para>The choice is held for the session whether or not it reaches the file, for the reason
/// <see cref="DrawnSetting{T}"/> gives.</para>
/// </summary>
internal sealed class RailFolding
{
    private readonly PreferenceService _preferences;
    private readonly Action<bool> _show;
    private RailChoice _choice;
    private double _width = RailFold.OpenWidth;
    private bool? _shown;

    /// <param name="preferences">Where the choice is kept.</param>
    /// <param name="show">Opens the rail for true and folds it for false.</param>
    public RailFolding(PreferenceService preferences, Action<bool> show)
    {
        _preferences = preferences;
        _show = show;
        _choice = preferences.Current.Rail;
    }

    /// <summary>The window is now <paramref name="windowWidth"/> effective pixels wide.</summary>
    public void Fit(double windowWidth)
    {
        _width = windowWidth;

        var open = RailFold.IsOpen(_choice, windowWidth);

        if (open == _shown)
        {
            return;
        }

        _shown = open;
        _show(open);
    }

    /// <summary>The rail opened or folded, by its toggle or by <see cref="Fit"/>.</summary>
    public void Changed(bool open)
    {
        if (open == _shown)
        {
            return;
        }

        _shown = open;

        var chosen = RailFold.Chosen(open, _width);

        if (chosen == _choice)
        {
            return;
        }

        _choice = chosen;
        _preferences.Update(current => current with { Rail = chosen });
    }
}
