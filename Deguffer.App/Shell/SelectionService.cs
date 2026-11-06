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
    private readonly ISelectionStore _store;

    /// <summary>
    /// Starts reading the remembered ticks on a worker, and returns without waiting for them.
    ///
    /// <para>The app builds this before its window's first frame, and the file names every step of every
    /// row by its full path, so it is the largest of the settings files. Nothing needs it until the
    /// Storage page builds its first row, which a scan returns long after the read has finished.</para>
    /// </summary>
    public SelectionService(ISelectionStore store)
    {
        _store = store;
        Memory = Task.Run(() => new SelectionMemory(store.Load()));
    }

    /// <summary>
    /// What each row starts ticked as, once it is read. The preview waits for it before it builds a
    /// row, so no row starts at its tier default because the file had not been read yet.
    /// </summary>
    public Task<SelectionMemory> Memory { get; }

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
        // Only a row records a choice, and the preview builds none until the memory is read, so this
        // never waits.
        var memory = Memory.Result;

        memory.Remember(providerId, selection);

        _store.Save(memory.Entries);
    }
}
