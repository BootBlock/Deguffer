using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The real store's handling of what <c>pnputil</c> answers. Only the answers that name no package are
/// driven here: one that did would ask SetupAPI about this machine's own driver store.
/// </summary>
public sealed class DriverStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeSystemDirectories _system;

    public DriverStoreTests() => _system = new FakeSystemDirectories(_temp.Path);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AsksPnpUtilInTheWindowsDirectoryForTheListingItReads()
    {
        var runner = new FakeProcessRunner().Responding("/enum-drivers", "<PnpUtil />");

        var listing = await new DriverStore(runner, _system).ListAsync(CancellationToken.None);

        var (fileName, arguments) = Assert.Single(runner.Invocations);
        Assert.Equal(Path.Combine(_system.WindowsDirectory, "System32", "pnputil.exe"), fileName);
        Assert.Equal(PnpUtilDriverList.Arguments, arguments);
        Assert.Null(listing.Failure);
        Assert.Empty(listing.Packages);
    }

    [Fact]
    public async Task ReportsAPnpUtilThatFailedAsAFailure()
    {
        var runner = new FakeProcessRunner().Replying(_ => new CommandOutcome(5, string.Empty, "Access is denied."));

        var listing = await new DriverStore(runner, _system).ListAsync(CancellationToken.None);

        Assert.Contains("Access is denied.", listing.Failure, StringComparison.Ordinal);
    }

    /// <summary>An older pnputil prints its usage for an option it does not know, and exits cleanly.</summary>
    [Fact]
    public async Task ReportsAnAnswerThatIsNotTheListingAsAFailure()
    {
        var runner = new FakeProcessRunner().Responding("/enum-drivers", "Microsoft PnP Utility\r\n\r\nPNPUTIL [/add-driver <...>");

        var listing = await new DriverStore(runner, _system).ListAsync(CancellationToken.None);

        Assert.NotNull(listing.Failure);
        Assert.Empty(listing.Packages);
    }
}
