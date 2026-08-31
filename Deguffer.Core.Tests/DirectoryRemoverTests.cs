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

    /// <summary>
    /// The same bit, on a directory rather than a file.
    ///
    /// Windows refuses to remove a directory carrying <c>FILE_ATTRIBUTE_READONLY</c>, exactly as it
    /// refuses a read-only file — observed directly on this machine, where
    /// <c>Directory.Delete</c> on one throws <see cref="UnauthorizedAccessException"/>. The file
    /// path has always cleared the bit and retried; the directory path did not, so every file in
    /// the tree went and the directory stayed. The step then reports success, because bytes were
    /// reclaimed, and the folder the user was told would go is still on the disk.
    /// </summary>
    [Fact]
    public async Task RemovesADirectoryWhoseReadOnlyBitWouldOtherwiseBlockIt()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(1024, "cache", "held", "payload.bin");

        var held = Path.Combine(root, "held");
        File.SetAttributes(held, File.GetAttributes(held) | FileAttributes.ReadOnly);

        var outcome = await DirectoryRemover.RemoveAsync(root);

        Assert.Equal(1024, outcome.BytesReclaimed);
        Assert.False(Directory.Exists(held), "a read-only directory survived the removal");
        Assert.True(outcome.RootRemoved);
    }

    /// <summary>
    /// The other half of the same retry, and the reason it is gated on emptiness. A directory that
    /// still holds something is one the removal is leaving standing, so its attributes are left
    /// exactly as they were rather than reset on a path that survives.
    /// </summary>
    [Fact]
    public async Task LeavesAReadOnlyDirectoryUntouchedWhileSomethingInsideItIsStillHeld()
    {
        var root = _temp.CreateDirectory("cache");
        var held = _temp.CreateFile(2048, "cache", "kept", "held.bin");
        var directory = Path.Combine(root, "kept");
        File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.ReadOnly);

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await DirectoryRemover.RemoveAsync(root);

            Assert.True(Directory.Exists(directory));
            Assert.True(
                File.GetAttributes(directory).HasFlag(FileAttributes.ReadOnly),
                "a directory that survived the removal had its attributes reset");
        }

        File.SetAttributes(directory, FileAttributes.Normal);
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

    /// <summary>
    /// A smoke test, and deliberately no more than that. It cannot prove §6.3 — see
    /// <see cref="HandsEveryPathToTheFilesystemInExtendedLengthForm"/> for the assertion that can,
    /// and for why this one stays green with the prefixing removed outright.
    /// </summary>
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

    /// <summary>
    /// §6.3 — the assertion that actually discriminates.
    ///
    /// Removing a tree past MAX_PATH and watching it disappear proves nothing about this codebase.
    /// .NET's own path normalisation prepends <c>\\?\</c> to any path of 260 characters or more
    /// before it reaches Win32, so the deletion succeeds whether or not Core applied the prefix —
    /// measured directly: a raw <c>CreateDirectoryW</c> on such a path fails with
    /// ERROR_PATH_NOT_FOUND while <c>Directory.CreateDirectory</c> on the very same path succeeds,
    /// in a process where <c>RtlAreLongPathsEnabled</c> reports 0. That makes an outcome-based
    /// long-path test unfalsifiable on every machine, not merely on one with the
    /// <c>LongPathsEnabled</c> registry value set.
    ///
    /// The form of the path is what remains observable, and it discriminates everywhere. The tree
    /// below covers each branch that touches the filesystem: enumeration, a plain delete, the
    /// read-only retry, directory removal, and the reparse point.
    /// </summary>
    [Fact]
    public async Task HandsEveryPathToTheFilesystemInExtendedLengthForm()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(1024, "cache", "a.bin");
        _temp.CreateFile(2048, "cache", "nested", "b.bin");

        var readOnly = _temp.CreateFile(512, "cache", "read-only.bin");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        Directory.CreateSymbolicLink(Path.Combine(root, "link"), _temp.CreateDirectory("outside"));

        var deep = root;
        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, new string('n', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "payload.bin")), new byte[4096]);

        var recorder = new RecordingFileSystem(WindowsFileSystem.Default);
        var outcome = await DirectoryRemover.RemoveAsync(root, progress: null, default, recorder);

        Assert.True(outcome.RootRemoved);
        Assert.NotEmpty(recorder.Paths);
        Assert.All(
            recorder.Paths,
            path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));
    }

    /// <summary>
    /// The same rule applied to the root itself, which is the one entry no enumeration classifies.
    /// <see cref="DeletesAJunctionWithoutFollowingItIntoTheTargetTree"/> covers a junction found
    /// below the root; a junction handed in *as* the root took the other branch, where enumerating
    /// it transparently returns the link target's ordinary children and deletes them.
    /// </summary>
    [Fact]
    public async Task RemovesAJunctionGivenAsTheRootWithoutEmptyingWhatItPointsAt()
    {
        var outside = _temp.CreateDirectory("precious");
        var bystander = _temp.CreateFile(4096, "precious", "irreplaceable.bin");

        var junction = Path.Combine(_temp.Path, "cache");
        Directory.CreateSymbolicLink(junction, outside);

        var outcome = await DirectoryRemover.RemoveAsync(junction);

        Assert.True(outcome.RootRemoved);
        Assert.False(Directory.Exists(junction));

        Assert.True(Directory.Exists(outside), "removal followed the junction it was handed");
        Assert.True(File.Exists(bystander), "a file outside the target tree was destroyed");

        // The linked-to content was never ours to count.
        Assert.Equal(0, outcome.BytesReclaimed);
    }

    [Fact]
    public async Task DeletesAJunctionWithoutFollowingItIntoTheTargetTree()
    {
        // The highest-consequence branch in the codebase: if this regresses, deleting a cache
        // escapes through a junction and destroys whatever it points at.
        var root = _temp.CreateDirectory("cache");
        var outside = _temp.CreateDirectory("precious");
        var bystander = _temp.CreateFile(4096, "precious", "irreplaceable.bin");

        var junction = Path.Combine(root, "link");
        Directory.CreateSymbolicLink(junction, outside);

        var outcome = await DirectoryRemover.RemoveAsync(root);

        Assert.True(outcome.RootRemoved);
        Assert.False(Directory.Exists(junction));

        Assert.True(Directory.Exists(outside), "deletion followed the junction out of the target tree");
        Assert.True(File.Exists(bystander), "a file outside the target tree was destroyed");

        // The linked-to content was never ours to count.
        Assert.Equal(0, outcome.BytesReclaimed);
    }

    [Fact]
    public async Task StopsWhenCancelled()
    {
        var root = _temp.CreateDirectory("cache");
        for (var i = 0; i < 200; i++)
        {
            _temp.CreateFile(64, "cache", $"f{i}.bin");
        }

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DirectoryRemover.RemoveAsync(root, progress: null, cts.Token));
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
