using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// The grain decides whether a row's steps are offered one by one, and it replaced a rule that read
/// the answer off the number of steps. The case that rule got wrong is the single item: one superseded
/// build is the whole row, and it was never listed as an item to choose.
/// </summary>
public class StepGrainTests
{
    [Theory]
    [InlineData(StepGrain.Items, 0, false)]
    [InlineData(StepGrain.Items, 1, true)]
    [InlineData(StepGrain.Items, 40, true)]
    [InlineData(StepGrain.Parts, 0, false)]
    [InlineData(StepGrain.Parts, 1, false)]
    [InlineData(StepGrain.Parts, 2, true)]
    public void OffersEachStepOnlyWhereThereIsAChoiceToMake(StepGrain grain, int steps, bool offered)
    {
        Assert.Equal(offered, grain.OffersEachStep(steps));
    }

    /// <summary>
    /// Every shipped provider states its grain, and this pins which of them list items. A provider
    /// whose steps are parts of one cache and which declared <see cref="StepGrain.Items"/> would turn
    /// its row into a list of folder names nobody chooses between, and one that lists items and
    /// declared <see cref="StepGrain.Parts"/> would hide a lone item behind its row.
    ///
    /// <para>Every provider that lets an item be kept is here, which is what keeps a kept item listed
    /// where it can be released.</para>
    /// </summary>
    [Fact]
    public void TheProvidersWhoseStepsAreItemsAreExactlyThese()
    {
        string[] items =
        [
            "affinity-model-cache",
            "azure-functions-tools",
            "cargo-target",
            "claude-code-file-history",
            "dotnet-obj",
            "node-modules",
            "playwright",
            "python-venv",
            "roslyn-cache",
            "squirrel-superseded-versions",
            "steam-shader-cache",
            "unity-library",
            "unreal-intermediate",
            "unreal-project-ddc",
            "vscode-cpptools",
        ];

        var declared = CleanupPlanner.CreateDefault().Providers
            .Where(provider => provider.Grain == StepGrain.Items)
            .Select(provider => provider.Id)
            .Order(StringComparer.Ordinal);

        Assert.Equal(items, declared);
    }
}
