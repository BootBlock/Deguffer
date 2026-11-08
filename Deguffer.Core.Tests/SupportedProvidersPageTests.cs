using Deguffer.Core.Execution;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The wiki lists every provider Deguffer ships, and a provider added or removed without the list
/// changing leaves users reading about a product that does not exist. The page is published from
/// <see cref="SupportedProvidersPage.RelativePath"/>, so this test holds that file to the shipped set.
/// </summary>
public sealed class SupportedProvidersPageTests
{
    /// <summary>Set to 1 to rewrite the page from the shipped providers instead of comparing it.</summary>
    private const string RewriteVariable = "DEGUFFER_WRITE_PROVIDER_PAGE";

    [Fact]
    public void TheCommittedPageListsEveryShippedProvider()
    {
        using var temp = new TempDirectory();
        var planner = CleanupPlanner.CreateDefault(
            liveTrees: FakeLiveTreeInspector.NothingLive,
            environment: new FakeUserEnvironment(temp.Path));
        var expected = SupportedProvidersPage.Render(planner.Providers);
        var path = Path.Combine(MarkdownGuide.RepositoryRoot, SupportedProvidersPage.RelativePath);

        if (Environment.GetEnvironmentVariable(RewriteVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, expected);
            return;
        }

        var committed = MarkdownGuide.At(path).Text;

        Assert.True(
            committed == expected,
            $"{SupportedProvidersPage.RelativePath} does not match the providers Deguffer ships. Run the "
                + $"tests with {RewriteVariable}=1 to rewrite it, then publish it to the wiki's "
                + "Supported-providers page.");
    }

    /// <summary>
    /// Every shipped provider appears on the page by name, so a rendering change cannot drop rows while
    /// the comparison above still passes against a page rewritten from the same rendering.
    /// </summary>
    [Fact]
    public void ThePageNamesEveryShippedProvider()
    {
        using var temp = new TempDirectory();
        var planner = CleanupPlanner.CreateDefault(
            liveTrees: FakeLiveTreeInspector.NothingLive,
            environment: new FakeUserEnvironment(temp.Path));

        var page = SupportedProvidersPage.Render(planner.Providers);
        var rows = page.Split('\n').Where(line => line.StartsWith("| ", StringComparison.Ordinal)).ToList();

        Assert.True(planner.Providers.Count > 100, $"only {planner.Providers.Count} providers were built");
        foreach (var provider in planner.Providers)
        {
            Assert.Contains(rows, row => row.StartsWith($"| {provider.Name} |", StringComparison.Ordinal));
        }

        Assert.Contains($"Deguffer knows {planner.Providers.Count} locations.", page, StringComparison.Ordinal);
    }
}
