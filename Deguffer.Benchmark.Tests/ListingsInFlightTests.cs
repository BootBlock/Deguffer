using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Benchmark.Tests;

/// <summary>The most listings a run had in flight at once, which is what a walk's thread count is for.</summary>
public sealed class ListingsInFlightTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Two listings held open together count as two, and a third after both have ended counts as one,
    /// so the figure is listings at once rather than listings in all.
    /// </summary>
    [Fact]
    public async Task CountsTheListingsInFlightTogether()
    {
        using var bothIn = new CountdownEvent(2);
        using var released = new ManualResetEventSlim();
        var listings = new ListingsInFlight((_, _, _) =>
        {
            bothIn.Signal();
            released.Wait(Patience);
            return new ListingEnd(PathPresence.Present, Denied: false);
        });

        var first = Task.Run(() => listings.List("a", new EnumerationOptions(), []));
        var second = Task.Run(() => listings.List("b", new EnumerationOptions(), []));
        Assert.True(bothIn.Wait(Patience));
        released.Set();
        await Task.WhenAll(first, second);

        var alone = new ListingsInFlight(static (_, _, _) => new ListingEnd(PathPresence.Present, Denied: false));
        alone.List("c", new EnumerationOptions(), []);
        alone.List("d", new EnumerationOptions(), []);

        Assert.Equal(2, listings.Peak);
        Assert.Equal(1, alone.Peak);
    }

    /// <summary>A listing that throws is counted out again, or every later figure would be one too high.</summary>
    [Fact]
    public void AListingThatThrowsIsCountedOut()
    {
        var calls = 0;
        var listings = new ListingsInFlight((_, _, _) => ++calls == 1
            ? throw new IOException("The listing failed.")
            : new ListingEnd(PathPresence.Present, Denied: false));

        Assert.Throws<IOException>(() => listings.List("a", new EnumerationOptions(), []));
        listings.List("b", new EnumerationOptions(), []);

        Assert.Equal(1, listings.Peak);
    }
}
