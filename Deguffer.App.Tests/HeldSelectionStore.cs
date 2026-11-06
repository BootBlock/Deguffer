using Deguffer.Core.Configuration;

namespace Deguffer.App.Tests;

/// <summary>
/// Reads what another store reads, once the test lets it. The remembered ticks are read on a worker
/// from launch, and what the window and the Storage page do while that read is still going is the
/// order this exists to arrange.
/// </summary>
internal sealed class HeldSelectionStore(ISelectionStore inner) : ISelectionStore, IDisposable
{
    private readonly ManualResetEventSlim _released = new(initialState: false);

    /// <summary>Let the read finish, and every read after it.</summary>
    public void Release() => _released.Set();

    public IReadOnlyDictionary<string, RememberedSelection> Load()
    {
        // Bounded, so a page that never lets the read finish fails its test rather than hanging it.
        if (!_released.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("The test never released the read of the remembered ticks.");
        }

        return inner.Load();
    }

    public bool Save(IReadOnlyDictionary<string, RememberedSelection> selections) => inner.Save(selections);

    public void Dispose() => _released.Dispose();
}
