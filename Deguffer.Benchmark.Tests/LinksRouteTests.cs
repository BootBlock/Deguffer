using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The links route counts each file it read once, by either route, so the two results name the same
/// thing per second and can be compared.
/// </summary>
public sealed class LinksRouteTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(nameof(LinkRead.Name))]
    [InlineData(nameof(LinkRead.Handle))]
    public void CountsEveryFileOnceByEitherRoute(string readBy)
    {
        var root = _temp.CreateDirectory("store");
        _temp.CreateFile(10, "store", "a", "one.bin");
        _temp.CreateFile(10, "store", "a", "two.bin");
        var shared = _temp.CreateFile(10, "store", "b", "three.bin");
        HardLink.To(shared, Path.Combine(_temp.CreateDirectory("project"), "three.bin"));

        var tally = LinksRoute.Run(root, Enum.Parse<LinkRead>(readBy), CancellationToken.None);

        Assert.Equal(3, tally.Items);
        Assert.True(tally.Complete);
    }
}
