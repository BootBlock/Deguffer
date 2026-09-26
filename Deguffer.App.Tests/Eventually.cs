namespace Deguffer.App.Tests;

/// <summary>Waits, on whatever thread the test runs on, for something another thread brings about.</summary>
internal static class Eventually
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public static async Task HoldsAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Never came to pass: {what}.");
            }

            await Task.Delay(5);
        }
    }
}
