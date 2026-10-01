namespace Deguffer.Core.Execution;

/// <summary>An action that changes the machine, named so the window can say what is running.</summary>
public enum RunningAction
{
    /// <summary>A clean started on the Storage page.</summary>
    StorageClean,

    /// <summary>A removal of what was picked on the Explore page.</summary>
    ExploreRemoval,

    /// <summary>A removal of stale entries on the Installed apps page.</summary>
    EntryRemoval,

    /// <summary>A registry backup being written back on the Installed apps page.</summary>
    BackupRestore,

    /// <summary>Programs being uninstalled, one at a time, on the Installed apps page.</summary>
    Uninstall,
}

/// <summary>
/// What is changing the machine right now, whichever page started it.
///
/// <para>Every page is kept alive between visits, so a clean goes on running on the Storage page
/// while the user is on another one. Each page knows only whether it is busy itself, which let an
/// Elevate button on one page end the process under a clean or a removal on another, and closing
/// the window did the same. The deletion is confirmed by then, so nothing beyond it is lost, but
/// the end of the run is: the §5.6 verification and the report of what was removed. This is the
/// one answer every page and the window read instead.</para>
///
/// <para>Used from the window's thread only. Every action begins and ends there, and the
/// <see cref="Changed"/> handlers update commands bound to controls, which must run there too.</para>
/// </summary>
public sealed class RunningActions
{
    private readonly List<RunningAction> _running = [];

    /// <summary>Raised after an action begins or ends.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Whether the process may end now, by closing the window or by elevating: only while nothing
    /// is running. Both ways read this one answer, so neither can come to disagree with the other.
    /// </summary>
    public bool MayEndProcess => _running.Count == 0;

    /// <summary>What is running, oldest first.</summary>
    public IReadOnlyList<RunningAction> Current => [.. _running];

    /// <summary>
    /// Record that <paramref name="action"/> has begun. Disposing the result records that it has
    /// ended, and a second disposal does nothing, so a <c>using</c> covers every way out of the run.
    /// </summary>
    public IDisposable Begin(RunningAction action)
    {
        _running.Add(action);
        Changed?.Invoke(this, EventArgs.Empty);

        return new Ending(this, action);
    }

    private void End(RunningAction action)
    {
        _running.Remove(action);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Ending(RunningActions owner, RunningAction action) : IDisposable
    {
        private bool _ended;

        public void Dispose()
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            owner.End(action);
        }
    }
}
