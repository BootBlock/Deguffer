using Deguffer.Core.Configuration;

namespace Deguffer.App.Shell;

/// <summary>
/// What the Storage rows were left ticked as, and the one place that writes them back.
///
/// Separate from <see cref="PreferenceService"/> for the reason <see cref="SourceRootService"/> is:
/// this is not a presentation setting. It decides what a later scan offers pre-selected.
/// </summary>
public sealed class SelectionService
{
    private readonly SelectionStore _store;

    public SelectionService(SelectionStore store)
    {
        _store = store;
        Memory = new SelectionMemory(store.Load());
    }

    /// <summary>What each row starts ticked as, read as the preview builds its rows.</summary>
    public SelectionMemory Memory { get; }

    /// <summary>
    /// Record one row's choice.
    ///
    /// Applied first and persisted second, which inverts <see cref="SourceRootService"/> and does so
    /// deliberately. The user has already ticked the box: the choice is in effect on screen whether
    /// or not a file can be written, and refusing to hold it in memory would only make a rescan in
    /// this session undo what they just did. A failed write therefore costs them the choice at the
    /// next launch and nothing sooner.
    ///
    /// That is also why this reports nothing, where the other two services return whether the write
    /// took. There is nowhere useful to report it to: the Storage page's info bar carries §5.6's
    /// verification headline, which is not a thing to displace for a tick the user can see took
    /// effect.
    /// </summary>
    public void Remember(string providerId, RememberedSelection selection)
    {
        Memory.Remember(providerId, selection);

        _store.Save(Memory.Entries);
    }
}
