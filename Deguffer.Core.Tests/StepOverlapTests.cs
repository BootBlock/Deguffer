using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which steps of one plan may be under way together: removals Deguffer makes itself of folders that
/// neither hold nor sit inside each other, compared as folders and never as text, and nothing else.
/// </summary>
public sealed class StepOverlapTests : IDisposable
{
    private const string Source = @"C:\Users\testuser\src";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void RemovalsOfFoldersApartMayRunTogether()
    {
        var overlap = Overlap(
            new DeleteDirectoryStep($@"{Source}\app\obj", "obj"),
            new ClearDirectoryStep($@"{Source}\lib\cache", "cache"),
            new DeleteFileStep($@"{Source}\lib\crash.dmp", "dump"));

        Assert.True(overlap.MayStartBeside(1, [0]));
        Assert.True(overlap.MayStartBeside(2, [0, 1]));
    }

    /// <summary>
    /// A folder inside another is never removed beside it, whichever comes first in the plan, and nor is
    /// the same folder named twice. A prefix of the text is not a folder inside: <c>obj2</c> is beside
    /// <c>obj</c>.
    /// </summary>
    [Fact]
    public void AFolderInsideAnotherOrTheSameFolderIsNeverRemovedBesideIt()
    {
        var overlap = Overlap(
            new DeleteDirectoryStep($@"{Source}\app\obj", "obj"),
            new DeleteDirectoryStep($@"{Source}\app\obj\Debug", "Debug"),
            new DeleteDirectoryStep($@"{Source}\APP\OBJ", "obj again"),
            new DeleteDirectoryStep($@"{Source}\app\obj2", "obj2"));

        Assert.False(overlap.MayStartBeside(1, [0]));
        Assert.False(overlap.MayStartBeside(0, [1]));
        Assert.False(overlap.MayStartBeside(2, [0]));
        Assert.True(overlap.MayStartBeside(3, [0]));
    }

    /// <summary>
    /// An index removed with the folder it indexes is part of what that step destroys, so a step inside
    /// the index waits for it as one inside the folder does.
    /// </summary>
    [Fact]
    public void EveryPathAStepDestroysIsCompared()
    {
        var overlap = Overlap(
            new DeleteDirectoryStep($@"{Source}\app\out", "out") { IndexedBy = [$@"{Source}\app\.index"] },
            new DeleteDirectoryStep($@"{Source}\app\.index\shards", "shards"));

        Assert.False(overlap.MayStartBeside(1, [0]));
    }

    /// <summary>
    /// <c>T:</c> substituted for the source folder makes <c>T:\app\obj</c> the folder below it, which the
    /// text alone does not say.
    /// </summary>
    [Fact]
    public void AFolderReachedThroughASubstitutedDriveIsTheSameFolder()
    {
        var volumes = new FakeVolumeInventory().Substituting(@"T:\", Source);
        var overlap = StepOverlap.Of(
            [new DeleteDirectoryStep($@"{Source}\app\obj", "obj"), new DeleteDirectoryStep(@"T:\app\obj\Debug", "Debug")],
            volumes,
            EmptyFileSystem.Instance);

        Assert.False(overlap.MayStartBeside(1, [0]));
    }

    /// <summary>
    /// A junction makes two paths one folder. The step's path is followed to where Windows says it
    /// leads, so a removal through the junction waits for a removal of the folder it leads into.
    /// </summary>
    [Fact]
    public void AFolderReachedThroughAJunctionIsTheSameFolder()
    {
        var real = _temp.CreateDirectory("src", "app", "obj");
        _temp.CreateDirectory("src", "app", "obj", "Debug");
        var work = Path.Combine(_temp.Path, "work");
        Junction.ToDirectory(work, Path.Combine(_temp.Path, "src"));

        var overlap = StepOverlap.Of(
            [new DeleteDirectoryStep(real, "obj"), new DeleteDirectoryStep(Path.Combine(work, "app", "obj", "Debug"), "Debug")],
            new FakeVolumeInventory(),
            WindowsFileSystem.Default);

        Assert.False(overlap.MayStartBeside(1, [0]));
        Assert.False(overlap.MayStartBeside(0, [1]));
    }

    /// <summary>
    /// What a tool's command, the Recycle Bin or Disk Cleanup removes is not walked by Deguffer, so
    /// nothing can say it is apart from anything: each runs alone, after every step before it and
    /// before every step after it.
    /// </summary>
    [Fact]
    public void AStepDegufferDoesNotWalkRunsAlone()
    {
        var overlap = Overlap(
            new DeleteDirectoryStep($@"{Source}\app\obj", "obj"),
            new RunCommandStep("tool", "clean", "Clean") { WhenCancelled = CancelledCommand.Ended },
            new EmptyRecycleBinStep(@"D:\$Recycle.Bin\S-1-5-21-0", "bin"),
            new DiskCleanupStep(@"C:\Windows\Temp\leftovers", "leftovers") { Handler = "Temporary Setup Files", Volume = @"C:\" });

        Assert.False(overlap.MayStartBeside(1, [0]));
        Assert.False(overlap.MayStartBeside(0, [1]));
        Assert.False(overlap.MayStartBeside(2, [0]));
        Assert.False(overlap.MayStartBeside(3, [0]));

        // With nothing under way, any step may start.
        Assert.True(overlap.MayStartBeside(1, []));
    }

    /// <summary>
    /// A removal whose check first asks a program to let go makes the request once for every step that
    /// shares the check, so a step beside it would be held back by a program about to stop.
    /// </summary>
    [Fact]
    public void ARemovalWhoseCheckReleasesAProgramRunsAlone()
    {
        var releasing = new ReleasingProcessCheck(
            "sccache", "--stop-server", ["sccache"], new FakeProcessRunner(), new FakeProcessInspector(), TimeProvider.System);

        var overlap = Overlap(
            new DeleteDirectoryStep($@"{Source}\app\obj", "obj"),
            new ClearDirectoryStep(@"C:\Users\testuser\AppData\Local\Mozilla\sccache", "sccache") { UseCheck = releasing });

        Assert.False(overlap.MayStartBeside(1, [0]));
        Assert.False(overlap.MayStartBeside(0, [1]));
    }

    /// <summary>
    /// A share may be this computer's own disk under another name, so nothing can say which other path
    /// is the same folder, and a removal on one runs alone.
    /// </summary>
    [Fact]
    public void ARemovalOnAShareRunsAlone()
    {
        var overlap = Overlap(
            new DeleteDirectoryStep($@"{Source}\app\obj", "obj"),
            new DeleteDirectoryStep(@"\\localhost\C$\Users\testuser\src\lib\obj", "obj on a share"));

        Assert.False(overlap.MayStartBeside(1, [0]));
        Assert.False(overlap.MayStartBeside(0, [1]));
    }

    private static StepOverlap Overlap(params CleanupStep[] steps) =>
        StepOverlap.Of(steps, new FakeVolumeInventory(), EmptyFileSystem.Instance);
}
