namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Captures progress reports synchronously.
///
/// <see cref="Progress{T}"/> posts through a synchronization context — and with none installed, to
/// the thread pool — so a test using it races its assertions against the callbacks. Reports arrive
/// here from scanner worker threads, hence the lock.
/// </summary>
public sealed class ProgressRecorder<T> : IProgress<T>
{
    private readonly List<T> _reports = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<T> Reports
    {
        get
        {
            lock (_gate)
            {
                return [.. _reports];
            }
        }
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            _reports.Add(value);
        }
    }
}
