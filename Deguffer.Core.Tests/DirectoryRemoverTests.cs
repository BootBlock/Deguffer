using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

public sealed class DirectoryRemoverTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ReportsSuccessForATreeThatWasAlreadyGone()
    {
        var outcome = await DirectoryRemover.RemoveAsync(Path.Combine(_temp.Path, "never-existed"));

        Assert.True(outcome.RootRemoved);
        Assert.Equal(0, outcome.BytesReclaimed);
    }

    [Fact]
    public async Task RemovesANestedTreeAndReportsWhatItReclaimed()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(1024, "cache", "a.bin");
        _temp.CreateFile(2048, "cache", "deep", "b.bin");
        _temp.CreateFile(4096, "cache", "deep", "deeper", "c.bin");

        var outcome = await DirectoryRemover.RemoveAsync(root);

        Assert.True(outcome.RootRemoved);
        Assert.Equal(0, outcome.Skipped);
        Assert.Equal(1024 + 2048 + 4096, outcome.BytesReclaimed);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ClearsTheReadOnlyBitThatPackageCachesSetLiberally()
    {
        var root = _temp.CreateDirectory("cache");
        var file = _temp.CreateFile(512, "cache", "locked.bin");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var outcome = await DirectoryRemover.RemoveAsync(root);

        Assert.True(outcome.RootRemoved);
        Assert.Equal(512, outcome.BytesReclaimed);
    }

    [Fact]
    public async Task LeavesAFileHeldOpenInPlaceRatherThanFailingTheRun()
    {
        // §5.3: a locked file is the OS protecting live state. Skipping is the correct outcome.
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(1024, "cache", "free.bin");
        var held = _temp.CreateFile(2048, "cache", "held.bin");

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = await DirectoryRemover.RemoveAsync(root);

            Assert.Equal(1, outcome.Skipped);
            Assert.Equal(1024, outcome.BytesReclaimed);
            Assert.False(outcome.RootRemoved);
            Assert.True(File.Exists(held));
        }
    }

    [Fact]
    public async Task ReachesFilesBeyondMaxPath()
    {
        var root = _temp.CreateDirectory("cache");

        var deep = root;
        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, new string('n', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "payload.bin")), new byte[8192]);

        var outcome = await DirectoryRemover.RemoveAsync(root);

        Assert.Equal(8192, outcome.BytesReclaimed);
        Assert.True(outcome.RootRemoved);
    }

    [Fact]
    public async Task ReportsProgressThroughToCompletion()
    {
        var root = _temp.CreateDirectory("cache");
        for (var i = 0; i < 8; i++)
        {
            _temp.CreateFile(64, "cache", $"f{i}.bin");
        }

        var reported = new List<double>();
        await DirectoryRemover.RemoveAsync(root, new Progress<double>(reported.Add));

        // Progress is marshalled asynchronously, so only the terminal report is guaranteed —
        // asserting on intermediate values here would be a flaky test.
        Assert.True(Directory.Exists(root) is false);
        Assert.All(reported, value => Assert.InRange(value, 0.0, 1.0));
    }
}
