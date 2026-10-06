using Deguffer.Core.VirtualDisks;

namespace Deguffer.Core.Tests;

/// <summary>
/// Reading <c>docker system df --format "{{json .}}"</c>, whose sizes are Docker's <c>HumanSize</c>:
/// powers of 1000, no space before the unit in current releases and one in older ones, and a share in
/// brackets after a reclaimable figure.
/// </summary>
public sealed class DockerDiskUsageTests
{
    [Theory]
    [InlineData("0B", 0)]
    [InlineData("212B", 212)]
    [InlineData("16.43MB", 16_430_000)]
    [InlineData("16.43 MB", 16_430_000)]
    [InlineData("1.2GB", 1_200_000_000)]
    [InlineData("11.63MB (70%)", 11_630_000)]
    [InlineData("3kB", 3_000)]
    [InlineData("1.5TB", 1_500_000_000_000)]
    public void ReadsASizeAsDockerWritesIt(string text, long bytes) =>
        Assert.Equal(bytes, DockerDiskUsage.Bytes(text));

    [Theory]
    [InlineData("")]
    [InlineData("N/A")]
    [InlineData("12 parsecs")]
    [InlineData("-1GB")]
    public void DoesNotGuessAtASizeItCannotRead(string text) =>
        Assert.Null(DockerDiskUsage.Bytes(text));

    [Fact]
    public void AddsUpEveryKindDockerReports()
    {
        var usage = DockerDiskUsage.Parse(
            """
            {"Active":"1","Reclaimable":"1GB (50%)","Size":"2GB","TotalCount":"4","Type":"Images"}
            {"Active":"0","Reclaimable":"250MB","Size":"250MB","TotalCount":"9","Type":"Build Cache"}
            """);

        Assert.NotNull(usage);
        Assert.Equal(["Images", "Build Cache"], usage.Rows.Select(row => row.Type));
        Assert.Equal(2_250_000_000, usage.Size);
        Assert.Equal(1_250_000_000, usage.Reclaimable);
    }

    /// <summary>A changed format reads as "Docker did not say", never as an empty disk.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("TYPE  TOTAL  ACTIVE  SIZE  RECLAIMABLE")]
    [InlineData("""{"Type":"Images","Size":"2GB"}""")]
    [InlineData("""{"Type":"Images","Size":"2GB","Reclaimable":"1GB"}""" + "\nnot json")]
    public void AnAnswerItCannotReadIsNoAnswer(string output) =>
        Assert.Null(DockerDiskUsage.Parse(output));
}
