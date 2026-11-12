namespace Deguffer.Core.Providers;

/// <summary>
/// What Windows says its Delivery Optimization cache holds.
///
/// <para>Behind an interface because the reading reaches the machine's Delivery Optimization service,
/// and a test that reached the real one would plan against the cache of whoever ran the suite.
/// <see cref="WindowsDeliveryOptimizationStatus"/> is the machine's.</para>
/// </summary>
public interface IDeliveryOptimizationStatus
{
    /// <summary>
    /// Asks Windows what the cache holds, and says why where it would not answer. Blocks on a call into
    /// a system service in another process, so a caller on a thread that draws calls it off that thread.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled before Windows answered.</exception>
    DeliveryOptimizationReading Read(CancellationToken ct);
}
