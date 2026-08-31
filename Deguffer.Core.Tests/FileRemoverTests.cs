using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Removing one named file, which arrived with <c>C:\Windows\MEMORY.DMP</c>.
///
/// The rules are <see cref="DirectoryRemover"/>'s, applied to a subject that cannot partially
/// succeed: §6.3's extended-length form on every path that reaches Win32, §5.3's "held open is
/// skipped rather than failed", and a link removed as a link rather than followed.
/// </summary>
public sealed class FileRemoverTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task RemovesTheFileAndReportsItsLength()
    {
        var file = _temp.CreateFile(8192, "dumps", "MEMORY.DMP");

        var outcome = await FileRemover.RemoveAsync(file);

        Assert.True(outcome.Removed);
        Assert.Equal(8192, outcome.BytesReclaimed);
        Assert.Equal(0, outcome.Skipped);
        Assert.False(File.Exists(file));
    }

    /// <summary>
    /// A file that went between planning and execution. The post-condition the caller wants — the
    /// named path is gone — holds, so this is a success with nothing reclaimed rather than a skip.
    /// </summary>
    [Fact]
    public async Task AFileThatIsAlreadyGoneCountsAsRemoved()
    {
        var outcome = await FileRemover.RemoveAsync(Path.Combine(_temp.Path, "never-existed.dmp"));

        Assert.True(outcome.Removed);
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.Equal(0, outcome.Skipped);
    }

    /// <summary>
    /// A directory sitting where a file was declared. It is not this step's to remove, and calling
    /// it removed would assert something untrue about a path that is still there.
    /// </summary>
    [Fact]
    public async Task ADirectoryWearingTheFileNameIsLeftAloneAndNotReportedAsRemoved()
    {
        var impostor = _temp.CreateDirectory("MEMORY.DMP");

        var outcome = await FileRemover.RemoveAsync(impostor);

        Assert.False(outcome.Removed);
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.True(Directory.Exists(impostor));
    }

    /// <summary>
    /// A link is removed as a link, so what it stands for is untouched — and the size reported is
    /// zero rather than the target's, because nothing on the far side was reclaimed.
    /// </summary>
    [Fact]
    public async Task ALinkIsRemovedWithoutTouchingWhatItPointsAt()
    {
        var bystander = _temp.CreateFile(4096, "precious", "irreplaceable.bin");
        var link = Path.Combine(_temp.CreateDirectory("dumps"), "MEMORY.DMP");

        File.CreateSymbolicLink(link, bystander);

        var outcome = await FileRemover.RemoveAsync(link);

        Assert.True(outcome.Removed);
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.False(File.Exists(link));
        Assert.True(File.Exists(bystander), "a file was deleted through a link");
    }

    /// <summary>§5.3: something holding the file open is the OS protecting live state, not a fault.</summary>
    [Fact]
    public async Task AFileHeldOpenIsSkippedRatherThanFailing()
    {
        var file = _temp.CreateFile(2048, "dumps", "MEMORY.DMP");

        using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);

        var outcome = await FileRemover.RemoveAsync(file);

        Assert.False(outcome.Removed);
        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.True(File.Exists(file));
    }

    /// <summary>A dump written with the read-only bit set is still this provider's to remove.</summary>
    [Fact]
    public async Task ClearsTheReadOnlyBitRatherThanGivingUp()
    {
        var file = _temp.CreateFile(1024, "dumps", "MEMORY.DMP");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var outcome = await FileRemover.RemoveAsync(file);

        Assert.True(outcome.Removed);
        Assert.Equal(1024, outcome.BytesReclaimed);
    }

    /// <summary>
    /// §6.3 — the assertion that actually discriminates, for the same reason
    /// <see cref="DirectoryRemoverTests.HandsEveryPathToTheFilesystemInExtendedLengthForm"/> gives:
    /// .NET prefixes a long path itself before it reaches Win32, so watching a deep file disappear
    /// stays green with the prefixing deleted outright. The form of the path is what remains
    /// observable, and it discriminates on every machine.
    /// </summary>
    [Fact]
    public async Task HandsEveryPathToTheFilesystemInExtendedLengthForm()
    {
        var deep = _temp.CreateDirectory("dumps");
        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, new string('n', 40));
        }

        var file = Path.Combine(deep, "MEMORY.DMP");
        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(file), new byte[4096]);
        File.SetAttributes(LongPath.Extended(file), FileAttributes.ReadOnly);

        var recorder = new RecordingFileSystem(WindowsFileSystem.Default);
        var outcome = await FileRemover.RemoveAsync(file, default, recorder);

        Assert.True(outcome.Removed);
        Assert.Equal(4096, outcome.BytesReclaimed);
        Assert.NotEmpty(recorder.Paths);
        Assert.All(
            recorder.Paths,
            path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));
    }
}
