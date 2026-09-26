using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// A driver store whose listing a test states, over folders the test built. The real one lists the
/// driver store of whoever runs the suite.
/// </summary>
public sealed class FakeDriverStore(Func<DriverStoreListing> listing) : IDriverStore
{
    public FakeDriverStore(params DriverPackage[] packages)
        : this(() => new DriverStoreListing(packages))
    {
    }

    /// <summary>How many times the store was listed, so a test can hold the provider to once a pass.</summary>
    public int Listings { get; private set; }

    public Task<DriverStoreListing> ListAsync(CancellationToken ct)
    {
        Listings++;
        return Task.FromResult(listing());
    }
}
