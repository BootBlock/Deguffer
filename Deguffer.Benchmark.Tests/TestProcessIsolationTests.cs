using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// This assembly closes the machine's scanner as it loads, as every test assembly does. The full set
/// of routes to it is asserted in <c>Deguffer.Core.Tests</c>.
/// </summary>
public sealed class TestProcessIsolationTests
{
    [Fact]
    public void ThisMachinesScannerIsClosedToThisTestProcess() =>
        Assert.Throws<InvalidOperationException>(() => DirectoryScanner.For(UserEnvironment.Current));
}
