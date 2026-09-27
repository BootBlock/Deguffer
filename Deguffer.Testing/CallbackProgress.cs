namespace Deguffer.Testing;

/// <summary>
/// Progress that runs the callback on the reporting thread. <see cref="Progress{T}"/> posts to the
/// thread pool, so a test that has to act on a report while the work is still on it, such as
/// cancelling a removal part-way, cannot use one.
/// </summary>
public sealed class CallbackProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
