using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.3's other half, which had no test in either scanner until the walk became one seam.
///
/// "Treat 'access denied' as normal and skip silently — a locked file is the OS protecting live
/// state" is a safety rule, and an untested one is a rule that can be deleted without anything
/// noticing: removing the catch filter outright left the whole suite green. It is tested here
/// rather than in a scanner's own class because both scanners now reach it through
/// <see cref="BoundedFileWalk"/>, so one test covers both.
/// </summary>
public sealed class BoundedFileWalkTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The scan reports what it could read and does not fail, which is §5.3 exactly. Both halves
    /// matter: an exception here would take a whole preview down over one protected folder, and
    /// counting the unreadable subtree would promise bytes no deletion could reclaim.
    /// </summary>
    [Fact]
    public async Task ARefusedDirectoryIsSkippedAndTheRestOfTheTreeStillCounts()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "readable.bin");
        var refused = _temp.CreateDirectory("cache", "refused");
        _temp.CreateFile(65536, "cache", "refused", "unreachable.bin");

        using var denied = new DeniedDirectory(refused);

        var measured = await ParallelEnumerationScanner.Default.MeasureAsync(root);

        Assert.Equal(4096, measured.Size.Logical);
    }
}
