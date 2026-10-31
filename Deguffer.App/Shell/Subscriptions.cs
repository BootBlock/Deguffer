namespace Deguffer.App.Shell;

/// <summary>
/// The event subscriptions a control holds while it is loaded, undone together by one
/// <see cref="Dispose"/>. Each is written once, as the pair that makes it and undoes it, so the
/// subscriptions made in <c>Loaded</c> cannot drift from those undone in <c>Unloaded</c>.
/// </summary>
internal sealed class Subscriptions : IDisposable
{
    private readonly List<Action> _undo = [];

    /// <summary>Make a subscription now, with <paramref name="subscribe"/>, and undo it with <paramref name="unsubscribe"/>.</summary>
    public Subscriptions Add(Action subscribe, Action unsubscribe)
    {
        subscribe();
        _undo.Add(unsubscribe);

        return this;
    }

    /// <summary>Undo every subscription, the last made first, and each only once.</summary>
    public void Dispose()
    {
        for (var i = _undo.Count - 1; i >= 0; i--)
        {
            _undo[i]();
        }

        _undo.Clear();
    }
}
