namespace Deguffer.Benchmark.Tests;

/// <summary>
/// What the command line accepts. A request the routes cannot honour is refused before anything is
/// timed, rather than timed as something else.
/// </summary>
public sealed class BenchmarkRequestTests
{
    [Theory]
    [InlineData("table", "C", "Table")]
    [InlineData("INDEX", "c:", "Index")]
    [InlineData("Explore", @"d:\", "Explore")]
    public void ARouteThatReadsTheTableTakesADriveInAnyOfItsForms(string route, string drive, string expected)
    {
        var request = BenchmarkRequest.Parse([route, drive], out var error);

        Assert.Null(error);
        Assert.Equal(Enum.Parse<Route>(expected), request!.Route);
        Assert.Equal(char.ToUpperInvariant(drive[0]) + @":\", request.Path);
        Assert.Equal(char.ToUpperInvariant(drive[0]), request.Drive);
        Assert.Equal(BenchmarkRequest.DefaultRuns, request.Runs);
    }

    /// <summary>The table belongs to a volume, so a folder would be timed as the whole volume.</summary>
    [Theory]
    [InlineData(@"C:\Users\testuser")]
    [InlineData("CD")]
    [InlineData("1")]
    [InlineData(@"\\server.test\share")]
    public void ARouteThatReadsTheTableRefusesAnythingButADrive(string target)
    {
        Assert.Null(BenchmarkRequest.Parse(["table", target], out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TheWalkTakesAFolderFullyQualified()
    {
        var request = BenchmarkRequest.Parse(["walk", "relative"], out _);

        Assert.Equal(Path.GetFullPath("relative"), request!.Path);
        Assert.True(Path.IsPathFullyQualified(request.Path));
    }

    /// <summary>By name only. Enum parsing would read a number or a list of names as a route.</summary>
    [Theory]
    [InlineData("scan")]
    [InlineData("1")]
    [InlineData("table, index")]
    [InlineData("")]
    public void ARouteMustBeNamed(string route)
    {
        Assert.Null(BenchmarkRequest.Parse([route, "C"], out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TheRunsCanBeChosen() =>
        Assert.Equal(3, BenchmarkRequest.Parse(["walk", @"C:\", "--runs", "3"], out _)!.Runs);

    [Theory]
    [InlineData("--runs", "0")]
    [InlineData("--runs", "1001")]
    [InlineData("--runs", "-1")]
    [InlineData("--runs", "many")]
    [InlineData("--runs")]
    [InlineData("--threads", "4")]
    public void AnythingElseAfterTheTargetIsRefused(params string[] rest)
    {
        Assert.Null(BenchmarkRequest.Parse(["walk", @"C:\", .. rest], out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ARouteWithoutATargetIsRefused()
    {
        Assert.Null(BenchmarkRequest.Parse(["walk"], out var error));
        Assert.NotNull(error);
    }
}
