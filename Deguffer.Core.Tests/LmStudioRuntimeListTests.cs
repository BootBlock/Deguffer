using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// The listing is the whole of what keeps the runtime LM Studio uses from being removed, so the rules
/// worth proving are that the mark is read wherever it lands and whatever it was decoded as, and that
/// anything unreadable fails the listing rather than dropping a row.
/// </summary>
public sealed class LmStudioRuntimeListTests
{
    /// <summary>What <c>lms runtime ls</c> printed on the surveyed machine, cut to five rows.</summary>
    private const string Surveyed =
        "LLM ENGINE                                        SELECTED    MODEL FORMAT\r\n"
        + "llama.cpp-win-x86_64-avx2@2.28.2                                  GGUF    \r\n"
        + "llama.cpp-win-x86_64-avx2@2.13.0                                  GGUF    \r\n"
        + "llama.cpp-win-x86_64-nvidia-cuda12-avx2@2.46.0       ✓            GGUF    \r\n"
        + "llama.cpp-win-x86_64-nvidia-cuda12-avx2@2.45.0                    GGUF    \r\n"
        + "llama.cpp-win-x86_64-vulkan-avx2@2.13.0                           GGUF    \r\n";

    [Fact]
    public void ReadsEveryRuntimeAndTheOneMarkedSelected()
    {
        var runtimes = LmStudioRuntimeList.TryRead(Surveyed);

        Assert.NotNull(runtimes);
        Assert.Equal(
            [
                new LmStudioRuntime("llama.cpp-win-x86_64-avx2", "2.28.2", false),
                new LmStudioRuntime("llama.cpp-win-x86_64-avx2", "2.13.0", false),
                new LmStudioRuntime("llama.cpp-win-x86_64-nvidia-cuda12-avx2", "2.46.0", true),
                new LmStudioRuntime("llama.cpp-win-x86_64-nvidia-cuda12-avx2", "2.45.0", false),
                new LmStudioRuntime("llama.cpp-win-x86_64-vulkan-avx2", "2.13.0", false),
            ],
            runtimes);
    }

    /// <summary>
    /// The pipe is read under the console's code page, where the check mark arrives as three other
    /// characters. Matching the mark itself would read that as nothing selected, and every version of
    /// the line in use would then be offered but for the newest.
    /// </summary>
    [Fact]
    public void ReadsTheMarkWhateverItWasDecodedAs()
    {
        var garbled = Surveyed.Replace("✓", "Γ£ô", StringComparison.Ordinal);

        var runtimes = LmStudioRuntimeList.TryRead(garbled);

        Assert.NotNull(runtimes);
        Assert.Equal("2.46.0", Assert.Single(runtimes, runtime => runtime.IsSelected).Version);
    }

    /// <summary>LM Studio keeps one selection per model format, so two runtimes can both be in use.</summary>
    [Fact]
    public void ReadsMoreThanOneSelectedRuntime()
    {
        const string listing =
            "LLM ENGINE                     SELECTED    MODEL FORMAT\n"
            + "llama.cpp-win-x86_64-avx2@2.1.0     ✓         GGUF\n"
            + "mlx-llm-mac-arm64@0.9.0             ✓         MLX\n";

        var runtimes = LmStudioRuntimeList.TryRead(listing);

        Assert.NotNull(runtimes);
        Assert.All(runtimes, runtime => Assert.True(runtime.IsSelected));
    }

    [Fact]
    public void ReadsAnEmptyListingAsNothingInstalled()
    {
        var runtimes = LmStudioRuntimeList.TryRead("No runtimes found.\r\n");

        Assert.NotNull(runtimes);
        Assert.Empty(runtimes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Failed to start or connect to local LM Studio API server.\n")]

    // A heading with no SELECTED column leaves nothing to read the selection from.
    [InlineData("LLM ENGINE    MODEL FORMAT\nllama.cpp-win-x86_64-avx2@2.1.0    GGUF\n")]
    public void RefusesOutputThatIsNotAListing(string output) =>
        Assert.Null(LmStudioRuntimeList.TryRead(output));

    /// <summary>
    /// A row that cannot be read fails the listing rather than being skipped, because the row skipped
    /// could be the selected runtime.
    /// </summary>
    [Theory]
    [InlineData("llama.cpp-win-x86_64-avx2                                  GGUF")]
    [InlineData("@2.1.0                                                     GGUF")]
    [InlineData("llama.cpp-win-x86_64-avx2@                                 GGUF")]
    [InlineData("   llama.cpp-win-x86_64-avx2@2.1.0                         GGUF")]
    public void RefusesAListingWithARowItCannotRead(string row)
    {
        var listing =
            "LLM ENGINE                                        SELECTED    MODEL FORMAT\n"
            + "llama.cpp-win-x86_64-nvidia-cuda12-avx2@2.46.0       ✓            GGUF\n"
            + row + "\n";

        Assert.Null(LmStudioRuntimeList.TryRead(listing));
    }

    /// <summary>
    /// A runtime listed twice could be selected on one row and not on the other, and the unselected
    /// row would then be offered.
    /// </summary>
    [Theory]
    [InlineData("llama.cpp-win-x86_64-nvidia-cuda12-avx2@2.46.0")]
    [InlineData("LLAMA.CPP-WIN-X86_64-NVIDIA-CUDA12-AVX2@2.46.0")]
    public void RefusesAListingThatNamesARuntimeTwice(string twice)
    {
        var listing =
            "LLM ENGINE                                        SELECTED    MODEL FORMAT\n"
            + "llama.cpp-win-x86_64-nvidia-cuda12-avx2@2.46.0       \u2713            GGUF\n"
            + twice + "                                    GGUF\n";

        Assert.Null(LmStudioRuntimeList.TryRead(listing));
    }
}
