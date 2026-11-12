using Deguffer.Core.Providers;

namespace Deguffer.Testing;

/// <summary>
/// A Delivery Optimization service whose answer a test states. The real one reports the cache of
/// whoever runs the suite.
/// </summary>
public sealed class FakeDeliveryOptimizationStatus(Func<DeliveryOptimizationReading> read) : IDeliveryOptimizationStatus
{
    private int _reads;

    /// <summary>Windows reporting <paramref name="bytes"/> in the cache at every reading.</summary>
    public static FakeDeliveryOptimizationStatus Reporting(long bytes) => new(() => DeliveryOptimizationReading.Of(bytes));

    /// <summary>How many times the service was asked, so a test can hold the run to the readings it needs.</summary>
    public int Reads => Volatile.Read(ref _reads);

    public DeliveryOptimizationReading Read(CancellationToken ct)
    {
        Interlocked.Increment(ref _reads);
        return read();
    }
}
