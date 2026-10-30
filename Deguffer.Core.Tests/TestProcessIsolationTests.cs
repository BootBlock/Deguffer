using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// No test measures the machine the suite runs on. A test that did would pass, and on the way write
/// its scratch paths into the real user's <c>scan-estimates.json</c> and share one volume index with
/// every parallel test that invalidates it, so the guard is the process refusing it rather than any
/// test noticing.
/// </summary>
public sealed partial class TestProcessIsolationTests
{
    /// <summary>
    /// Every way a test reaches this machine's scanner or size cache: by name, through the planner the
    /// app composes, and through a provider given no environment, which is the one a test writes
    /// without meaning to.
    /// </summary>
    [Fact]
    public void ThisMachinesScannerAndSizeCacheAreClosedToATestProcess()
    {
        Assert.Throws<InvalidOperationException>(() => DirectoryScanner.For(UserEnvironment.Current));
        Assert.Throws<InvalidOperationException>(() => DirectoryScanner.CreateDefault(UserEnvironment.Current));
        Assert.Throws<InvalidOperationException>(() => ScanEstimateCache.For(UserEnvironment.Current));
        Assert.Throws<InvalidOperationException>(() => new ScanEstimateCache(UserEnvironment.Current));
        Assert.Throws<InvalidOperationException>(() => CleanupPlanner.CreateDefault());
        Assert.Throws<InvalidOperationException>(() => new NpmCacheProvider());
    }

    /// <summary>The fence shuts out this machine alone, so a test's own machine still measures.</summary>
    [Fact]
    public async Task AnInventedMachineStillMeasures()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        File.WriteAllBytes(Path.Combine(temp.CreateDirectory("measured"), "file.bin"), new byte[100]);

        var result = await DirectoryScanner.For(environment).MeasureAsync(Path.Combine(temp.Path, "measured"));

        Assert.Equal(100, result.Size.Logical);
    }

    /// <summary>
    /// The fence is closed by each test assembly as it loads, so a test project that does not close
    /// it runs every test it holds against this machine. Read from the source, so a project added
    /// later fails here until it closes it too.
    /// </summary>
    [Fact]
    public void EveryTestProjectClosesTheFenceAsItLoads()
    {
        var projects = TestProjects.Directories().ToList();

        Assert.True(projects.Count >= 3, $"only {projects.Count} test projects found");
        Assert.All(projects, project => Assert.True(
            TestProjects.SourceFiles(project).Any(file => Initialiser().IsMatch(File.ReadAllText(file))),
            $"'{Path.GetFileName(project)}' has no [ModuleInitializer] calling TestProcess.Isolate()."));
    }

    [GeneratedRegex(@"\[ModuleInitializer\]\s*internal\s+static\s+void\s+\w+\(\)\s*=>\s*TestProcess\.Isolate\(\);")]
    private static partial Regex Initialiser();
}
