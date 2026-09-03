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
    /// Record one row's choice. Returns whether it reached disk.
    ///
    /// Applied first and persisted second, which inverts <see cref="SourceRootService"/> and does so
    /// deliberately. The user has already ticked the box: the choice is in effect on screen whether
    /// or not a file can be written, and refusing to hold it in memory would only make a rescan in
    /// this session undo what they just did. A failed write therefore costs them the choice at the
    /// next launch and nothing sooner.
    /// </summary>
    public bool Remember(string providerId, RememberedSelection selection)
    {
        Memory.Remember(providerId, selection);

        return _store.Save(Memory.Entries);
    }
}
