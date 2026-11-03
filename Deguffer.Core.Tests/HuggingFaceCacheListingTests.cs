using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// <c>hf cache ls --format json</c> as huggingface_hub 1.31, 1.32 and 2.4 print it. A listing that
/// names a repository this cannot name exactly is no listing to choose from, because the command finds
/// a repository by its id and the plan measures and protects a folder.
/// </summary>
public sealed class HuggingFaceCacheListingTests
{
    // Observed from huggingface_hub 2.4.0, with a model, a dataset and a figure in each unit form.
    private const string Observed =
        """
        [{"id": "dataset/hf-internal-testing/fixtures_image_utils", "repo_id": "hf-internal-testing/fixtures_image_utils", "repo_type": "dataset", "size": "365.0", "last_accessed": "6 minutes ago", "last_modified": "6 minutes ago", "refs": ["main"]}, {"id": "model/hf-internal-testing/tiny-random-LlamaForCausalLM", "repo_id": "hf-internal-testing/tiny-random-LlamaForCausalLM", "repo_type": "model", "size": "4.1M", "last_accessed": "a few seconds ago", "last_modified": "a few seconds ago", "refs": ["main"]}, {"id": "model/hf-internal-testing/tiny-random-bert", "repo_id": "hf-internal-testing/tiny-random-bert", "repo_type": "model", "size": "520.8K", "last_accessed": "5 minutes ago", "last_modified": "5 minutes ago", "refs": ["main"]}]
        """;

    [Fact]
    public void ReadsWhatTheToolPrints()
    {
        var repositories = HuggingFaceCacheListing.TryRead(Observed);

        Assert.NotNull(repositories);
        Assert.Equal(
            [
                new HuggingFaceRepository("dataset/hf-internal-testing/fixtures_image_utils", "hf-internal-testing/fixtures_image_utils", "dataset", 365),
                new HuggingFaceRepository("model/hf-internal-testing/tiny-random-LlamaForCausalLM", "hf-internal-testing/tiny-random-LlamaForCausalLM", "model", 4_100_000),
                new HuggingFaceRepository("model/hf-internal-testing/tiny-random-bert", "hf-internal-testing/tiny-random-bert", "model", 520_800),
            ],
            repositories);
    }

    /// <summary>The folder is the tool's <c>repo_folder_name</c>: the plural type and the parts joined with <c>--</c>.</summary>
    [Theory]
    [InlineData("model", "openai-community/gpt2", "models--openai-community--gpt2")]
    [InlineData("dataset", "rajpurkar/squad", "datasets--rajpurkar--squad")]
    [InlineData("model", "gpt2", "models--gpt2")]
    [InlineData("space", "org/demo", "spaces--org--demo")]
    public void NamesTheFolderTheToolKeepsARepositoryIn(string type, string repoId, string folder)
    {
        Assert.Equal(folder, new HuggingFaceRepository($"{type}/{repoId}", repoId, type, 0).Folder);
    }

    [Fact]
    public void ReadsAnEmptyCacheAsNoRepositories()
    {
        Assert.Equal([], HuggingFaceCacheListing.TryRead("[]\n"));
    }

    [Theory]
    [InlineData("365.0", 365)]
    [InlineData("0.0", 0)]
    [InlineData("520.8K", 520_800)]
    [InlineData("3.1G", 3_100_000_000)]
    [InlineData("1.2T", 1_200_000_000_000)]
    public void ReadsASizeAsTheToolPrintsIt(string printed, long bytes)
    {
        Assert.Equal(bytes, HuggingFaceCacheListing.TryReadSize(printed));
    }

    [Theory]
    [InlineData("3.1 GB")]
    [InlineData("3,1G")]
    [InlineData("-1.0")]
    [InlineData("1.0Q")]
    [InlineData("")]
    public void ReadsNothingThatIsNotASize(string printed)
    {
        Assert.Null(HuggingFaceCacheListing.TryReadSize(printed));
    }

    /// <summary>
    /// Each of these is a listing with one entry Deguffer could not name a folder for, or name to the
    /// command exactly, and the whole listing is refused rather than that entry skipped.
    /// </summary>
    [Theory]
    [InlineData("""{"id": "model/a/b", "repo_id": "a/b", "repo_type": "model", "size": "1.0K"}""", true)]
    [InlineData("""{"id": "model/a/c", "repo_id": "a/b", "repo_type": "model", "size": "1.0K"}""", false)]
    [InlineData("""{"id": "widget/a/b", "repo_id": "a/b", "repo_type": "widget", "size": "1.0K"}""", false)]
    [InlineData("""{"id": "model/a--x/b", "repo_id": "a--x/b", "repo_type": "model", "size": "1.0K"}""", false)]
    [InlineData("""{"id": "model/a/../b", "repo_id": "a/../b", "repo_type": "model", "size": "1.0K"}""", false)]
    [InlineData("""{"id": "model/a/b/c", "repo_id": "a/b/c", "repo_type": "model", "size": "1.0K"}""", false)]
    [InlineData("""{"id": "model/a/b c", "repo_id": "a/b c", "repo_type": "model", "size": "1.0K"}""", false)]
    [InlineData("""{"id": "model/a/b", "repo_id": "a/b", "repo_type": "model", "size": 1000}""", false)]
    [InlineData("""{"id": "model/a/b", "repo_id": "a/b", "repo_type": "model"}""", false)]
    public void RefusesAListingWithARepositoryItCannotName(string entry, bool read)
    {
        Assert.Equal(read, HuggingFaceCacheListing.TryRead($"[{entry}]") is not null);
    }

    /// <summary>The command matches ids without regard to case, so two that differ only in case are one repository to it.</summary>
    [Fact]
    public void RefusesAListingNamingOneRepositoryTwice()
    {
        Assert.Null(HuggingFaceCacheListing.TryRead(
            """
            [{"id": "model/Org/Model", "repo_id": "Org/Model", "repo_type": "model", "size": "1.0K"},
             {"id": "model/org/model", "repo_id": "org/model", "repo_type": "model", "size": "1.0K"}]
            """));
    }

    [Theory]
    [InlineData("Usage: hf cache ls [OPTIONS]")]
    [InlineData("{\"repos\": []}")]
    [InlineData("")]
    public void ReadsNothingFromOutputThatIsNotAListing(string output)
    {
        Assert.Null(HuggingFaceCacheListing.TryRead(output));
    }
}
