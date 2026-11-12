namespace Deguffer.Core.Providers;

/// <summary>What Windows said its Delivery Optimization cache holds.</summary>
/// <param name="Bytes">The cache's size, or null where Windows did not answer.</param>
/// <param name="Failure">Why Windows did not answer, written for the user, or null where it did.</param>
public sealed record DeliveryOptimizationReading(long? Bytes, string? Failure)
{
    public static DeliveryOptimizationReading Of(long bytes) => new(bytes, null);

    public static DeliveryOptimizationReading Unanswered(string why) => new(null, why);
}
