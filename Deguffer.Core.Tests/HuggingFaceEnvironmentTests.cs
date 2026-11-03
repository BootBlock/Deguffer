using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// <c>hf env</c> as huggingface_hub prints it on Windows, the paths changed to the fakes' invented
/// profile. The version decides whether anything is offered, and the hub cache is where every command
/// is sent, so a line that is missing must read as nothing said.
/// </summary>
public sealed class HuggingFaceEnvironmentTests
{
    private const string Observed =
        """

        Copy-and-paste the text below in your GitHub issue.

        - huggingface_hub version: 2.4.0
        - Platform: Windows-10-10.0.26100-SP0
        - Python version: 3.10.11
        - Token path ?: C:/Users/testuser/.cache/huggingface\token
        - Has saved token ?: False
        - httpx2: 2.14.0
        - hf_xet: 1.8.0
        - ENDPOINT: https://huggingface.co
        - HF_HUB_CACHE: C:/Users/testuser/.cache/huggingface\hub
        - HF_ASSETS_CACHE: C:/Users/testuser/.cache/huggingface\assets
        - HF_TOKEN_PATH: C:/Users/testuser/.cache/huggingface\token
        - HF_STORED_TOKENS_PATH: C:/Users/testuser/.cache/huggingface\stored_tokens
        - HF_HUB_OFFLINE: False

        """;

    [Fact]
    public void ReadsTheVersionAndTheFolders()
    {
        var environment = HuggingFaceEnvironment.Read(Observed.ReplaceLineEndings("\r\n"));

        Assert.Equal(new Version(2, 4), environment.Version);
        Assert.True(environment.KnowsSharedBlobs);
        Assert.Equal(@"C:\Users\testuser\.cache\huggingface\hub", environment.HubCache);
        Assert.Equal(@"C:\Users\testuser\.cache\huggingface\assets", environment.Assets);
        Assert.Equal(
            [@"C:\Users\testuser\.cache\huggingface\token", @"C:\Users\testuser\.cache\huggingface\stored_tokens"],
            environment.Credentials);
    }

    [Theory]
    [InlineData("1.31.0", false)]
    [InlineData("1.31.9", false)]
    [InlineData("0.36.0", false)]
    [InlineData("1.32.0", true)]
    [InlineData("1.32.0rc0", true)]
    [InlineData("1.32.0.dev0", true)]
    [InlineData("10.0.0", true)]
    public void KnowsTheSharedStoreFrom132(string version, bool knows)
    {
        var environment = HuggingFaceEnvironment.Read(Observed.Replace("2.4.0", version, StringComparison.Ordinal));

        Assert.Equal(knows, environment.KnowsSharedBlobs);
    }

    [Fact]
    public void ReadsNothingThatWasNotSaid()
    {
        var environment = HuggingFaceEnvironment.Read("- Platform: Windows-10-10.0.26100-SP0\n- HF_HUB_CACHE: hub\n");

        Assert.Null(environment.Version);
        Assert.False(environment.KnowsSharedBlobs);

        // A relative value is resolved against the tool's own working directory, which is not Deguffer's.
        Assert.Null(environment.HubCache);
        Assert.Empty(environment.Credentials);
        Assert.Null(environment.Assets);
    }
}
