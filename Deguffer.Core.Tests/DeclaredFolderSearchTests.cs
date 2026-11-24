using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

public sealed class DeclaredFolderSearchTests
{
    /// <summary>
    /// The list Settings shows under the source folders names every location that waits for one, and
    /// nothing else. A provider added to the planner that reads only the source folders fails here
    /// until the reader deciding whether to add a folder can see it.
    /// </summary>
    [Fact]
    public void TheSourceFolderListNamesEveryLocationThatWaitsForASourceFolder()
    {
        using var temp = new TempDirectory();
        var planner = CleanupPlanner.CreateDefault(
            liveTrees: FakeLiveTreeInspector.NothingLive,
            environment: new FakeUserEnvironment(temp.Path));

        var waiting = planner.Providers
            .Where(provider => provider.IsAwaitingSourceFolders)
            .Select(provider => provider.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(
            waiting.SequenceEqual(DeclaredFolderSearch.InSourceFolders, StringComparer.Ordinal),
            $"DeclaredFolderSearch.InSourceFolders should read, in this order: {string.Join(", ", waiting.Select(name => $"\"{name}\""))}");
    }
}
