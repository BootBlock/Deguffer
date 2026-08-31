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

    /// <summary>
    /// A link whose target has gone — a redirected dump location whose far side was cleaned up.
    ///
    /// This pins the outcome and not the mechanism, and it is worth saying which. Windows reports a
    /// dangling link as an existing file of zero length, so the removal reaches the same answer
    /// whether or not <see cref="FileRemover"/> checks for a link first. The case is pinned because
    /// it is a real one and its correct answer is not obvious, not because it discriminates.
    /// </summary>
    [Fact]
    public async Task ALinkWhoseTargetHasGoneIsStillRemoved()
    {
        var target = _temp.CreateFile(4096, "precious", "irreplaceable.bin");
        var link = Path.Combine(_temp.CreateDirectory("dumps"), "MEMORY.DMP");

        File.CreateSymbolicLink(link, target);
        File.Delete(target);

        var outcome = await FileRemover.RemoveAsync(link);

        Assert.True(outcome.Removed);
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.False(LongPath.IsReparsePoint(link), "a dangling link was reported removed and left in place");
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
    /// The file goes between the probe and the delete — a dump WER tidied up, or an update
    /// finishing. <see cref="DirectoryRemover"/> treats the identical race as a success, and
    /// <see cref="DirectoryNotFoundException"/> derives from <see cref="IOException"/>, so without
    /// its own arm the skip path would claim Windows would not release a file that is not there.
    /// </summary>
    [Theory]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    public async Task AFileThatVanishesDuringTheDeleteIsRemovedRatherThanSkipped(Type thrown)
    {
        var file = _temp.CreateFile(2048, "dumps", "MEMORY.DMP");

        var outcome = await FileRemover.RemoveAsync(
            file, default, new VanishingFileSystem(WindowsFileSystem.Default, thrown));

        Assert.True(outcome.Removed);
        Assert.Equal(0, outcome.Skipped);
    }

    /// <summary>
    /// The real filesystem, except that the delete finds the path already gone. The race cannot be
    /// staged against a real disk, and <see cref="IFileSystem"/> exists precisely so a removal's
    /// behaviour is provable without one.
    /// </summary>
    private sealed class VanishingFileSystem(IFileSystem inner, Type thrown) : IFileSystem
    {
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public bool IsReparsePoint(string path) => inner.IsReparsePoint(path);

        public IReadOnlyList<FileSystemEntry> EnumerateEntries(string directory) =>
            inner.EnumerateEntries(directory);

        public long? TryGetFileLength(string path) => inner.TryGetFileLength(path);

        public void DeleteFile(string path) => throw (Exception)Activator.CreateInstance(thrown)!;

        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);

        public void ClearAttributes(string path) => inner.ClearAttributes(path);

        public FileAttributes? TryGetAttributes(string path) => inner.TryGetAttributes(path);
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
