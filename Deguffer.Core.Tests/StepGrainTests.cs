using Deguffer.Core.Execution;
using Deguffer.Testing;

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
            "autodesk-installers",
            "azure-functions-tools", "capcut-superseded-builds",
            "capture-one-cache",
            "cargo-target",
            "claude-code-conversations",
            "claude-code-file-history",
            "dart-tool",
            "dotnet-obj",
            "electron",
            "git-lfs-objects",
            "graphics-driver-installers",
            "jetbrains-superseded-system",
            "lmstudio-runtimes",
            "mypy-cache",
            "node-modules",
            "nox-environments",
            "other-account-profiles",
            "playwright",
            "puppeteer",
            "pytest-cache",
            "python-venv",
            "roslyn-cache",
            "ruff-cache",
            "squirrel-superseded-versions",
            "steam-library-artwork",
            "steam-shader-cache",
            "tox-environments",
            "typescript-typings",
            "unity-library",
            "unreal-intermediate",
            "unreal-project-ddc",
            "visual-studio-solution-cache",
            "vscode-cpptools",
            "vscode-extension-browsers",
        ];

        using var shipped = new ShippedProviders();
        var declared = shipped.Providers
            .Where(provider => provider.Grain == StepGrain.Items)
            .Select(provider => provider.Id)
            .Order(StringComparer.Ordinal);

        Assert.Equal(items, declared);
    }
}
