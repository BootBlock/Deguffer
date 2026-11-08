using Deguffer.Core.Execution;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Progress reported from several threads reaches the bar in an order that only ever grows, so a
/// report that arrives after a later one is dropped rather than pulling the bar back.
/// </summary>
public sealed class ForwardProgressTests
{
    private readonly List<double> _reported = [];

    [Fact]
    public void DropsAReportThatIsNotPastTheLastOnePassedOn()
    {
        var progress = new ForwardProgress(new CallbackProgress<double>(_reported.Add));

        progress.Report(0.5);
        progress.Report(0.25);
        progress.Report(0.5);
        progress.Report(0.75);

        Assert.Equal([0.5, 0.75], _reported);
    }

    /// <summary>The whole is always passed on, so a run that reports its end twice still ends whole.</summary>
    [Fact]
    public void AlwaysPassesOnTheWhole()
    {
        var progress = new ForwardProgress(new CallbackProgress<double>(_reported.Add));

        progress.Report(1.0);
        progress.Report(1.0);

        Assert.Equal([1.0, 1.0], _reported);
    }
}
