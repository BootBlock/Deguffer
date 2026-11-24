using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Viewing;

namespace Deguffer.App.Shell;

/// <summary>
/// The window's Back: the command the rail's back button, Alt+Left and the mouse's Back button all
/// run, enabled while <see cref="LinkTrail{T}"/> says Back leads somewhere, and showing where it
/// leads. The trail decides; this tells the window when that changes and where to go.
/// </summary>
internal sealed class LinkHistory
{
    private readonly LinkTrail<Destination> _trail;
    private readonly Action<Destination> _show;

    /// <param name="start">Where the window opens.</param>
    /// <param name="show">Shows a destination, and selects it on the rail, for Back.</param>
    public LinkHistory(Destination start, Action<Destination> show)
    {
        _trail = new LinkTrail<Destination>(start);
        _show = show;
        BackCommand = new RelayCommand(GoBack, () => _trail.CanGoBack);
    }

    /// <summary>Returns to where the nearest link was followed from. Enabled only while there is one.</summary>
    public IRelayCommand BackCommand { get; }

    /// <summary>The window is following a link to <paramref name="to"/>.</summary>
    public void Follow(Destination to) => Move(() => _trail.Follow(to));

    /// <summary>The reader chose <paramref name="to"/> on the rail.</summary>
    public void Choose(Destination to) => Move(() => _trail.Choose(to));

    private void GoBack()
    {
        Destination? place = null;
        Move(() => place = _trail.Back());

        if (place is { } shown)
        {
            _show(shown);
        }
    }

    /// <summary>
    /// Make <paramref name="move"/>, and say the command changed only where Back became offered or
    /// withdrawn: the rail's button is shown and hidden on that, and a second link changes neither.
    /// </summary>
    private void Move(Action move)
    {
        var could = _trail.CanGoBack;
        move();

        if (_trail.CanGoBack != could)
        {
            BackCommand.NotifyCanExecuteChanged();
        }
    }
}
