using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Viewing;

namespace Deguffer.App.Shell;

/// <summary>
/// The window's Back: where the window is, the links it followed from there, and the command the
/// rail's back button, Alt+Left and the mouse's Back button all run. <see cref="LinkTrail{T}"/>
/// decides what Back leads to, and this tells the window when that changes and where to go.
/// </summary>
internal sealed class LinkHistory
{
    private readonly LinkTrail<Destination> _trail = new();
    private readonly Action<Destination> _show;
    private Destination _here;

    /// <param name="start">Where the window opens.</param>
    /// <param name="show">Shows a destination, and selects it on the rail, for Back.</param>
    public LinkHistory(Destination start, Action<Destination> show)
    {
        _here = start;
        _show = show;
        BackCommand = new RelayCommand(GoBack, () => _trail.CanGoBack);
    }

    /// <summary>Returns to where the nearest link was followed from. Enabled only while there is one.</summary>
    public IRelayCommand BackCommand { get; }

    /// <summary>The window is following a link to <paramref name="to"/>.</summary>
    public void Follow(Destination to)
    {
        var changed = _trail.Follow(_here, to);
        _here = to;

        if (changed)
        {
            BackCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The reader chose <paramref name="to"/> on the rail, which forgets every link (see <see cref="LinkTrail{T}"/>).</summary>
    public void Choose(Destination to)
    {
        _here = to;

        if (_trail.Forget())
        {
            BackCommand.NotifyCanExecuteChanged();
        }
    }

    private void GoBack()
    {
        if (_trail.Back() is not { } place)
        {
            return;
        }

        // Where the window is before it goes there, so a link followed from the page Back shows is
        // followed from that page.
        _here = place;
        BackCommand.NotifyCanExecuteChanged();
        _show(place);
    }
}
