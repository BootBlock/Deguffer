using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A folder Windows will not let a removal list, which the removal skips (§5.3) and then has to
/// account for. Nothing inside it was seen, so nothing inside it was refused, and before the removal
/// said so a clear whose root would not list reported that it held nothing to clear, while a folder
/// inside a cleared tree vanished from the report.
/// </summary>
public sealed class UnlistedRemovalTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private RefusalRecord RefusalLog => RefusalRecord.For(new FakeUserEnvironment(_temp.Path));

    private static CleanupPlan PlanDeleting(CleanupStep step) => new()
    {
        ProviderId = "test",
        ProviderName = "Test",
        Tier = SafetyTier.RegenerableCache,
        WhatHappensOnNextUse = "Nothing.",
        Steps = [step],
    };

    private async Task<CleanupResult> Execute(CleanupStep step) =>
        await new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog)
            .ExecuteAsync(PlanDeleting(step), runReach: null, residue: null, progress: null, underway: null, default);

    /// <summary>
    /// The removal names the folder it could not list, in display form, as standing and as unlisted,
    /// and counts no folder refusal for it: one folder is one clause.
    /// </summary>
    [Fact]
    public async Task TheRemovalNamesAFolderItCouldNotList()
    {
        var root = _temp.CreateDirectory("cache");
        var free = _temp.CreateFile(1024, "cache", "free.bin");
        var locked = _temp.CreateDirectory("cache", "locked");
        var inside = _temp.CreateFile(2048, "cache", "locked", "unseen.bin");

        var outcome = await DirectoryRemover.RemoveAsync(
            root, fileSystem: new UnlistableFileSystem(WindowsFileSystem.Default, locked));

        Assert.Equal([locked], outcome.Unlisted);
        Assert.Contains(locked, outcome.LeftStanding);
        Assert.True(outcome.RefusedFolders.IsEmpty);
        Assert.True(outcome.Refused.IsEmpty);
        Assert.False(File.Exists(free));
        Assert.True(File.Exists(inside), "a file in a folder the removal could not list was removed");
        Assert.False(outcome.RootRemoved);
    }

    /// <summary>
    /// A folder Windows will neither list nor remove is one folder, so it is one clause: unlisted, and
    /// not counted again as a folder Windows refused.
    /// </summary>
    [Fact]
    public async Task AFolderThatWouldNotListIsNotCountedAgainAsRefused()
    {
        var root = _temp.CreateDirectory("cache");
        var locked = _temp.CreateDirectory("cache", "locked");
        _temp.CreateFile(2048, "cache", "locked", "unseen.bin");

        var fs = new RefusingFileSystem(
            new UnlistableFileSystem(WindowsFileSystem.Default, locked),
            new Dictionary<string, RefusalReason> { [locked] = RefusalReason.Denied });

        var outcome = await DirectoryRemover.RemoveAsync(root, fileSystem: fs);

        Assert.Equal([locked], outcome.Unlisted);
        Assert.True(outcome.RefusedFolders.IsEmpty);
    }

    /// <summary>
    /// A folder whose listing was refused and which then went held nothing, because Windows removes
    /// only an empty folder. Nothing is left in it to account for.
    /// </summary>
    [Fact]
    public async Task AnUnlistedFolderThatWentIsNotNamed()
    {
        var root = _temp.CreateDirectory("cache");
        var empty = _temp.CreateDirectory("cache", "empty");

        var outcome = await DirectoryRemover.RemoveAsync(
            root, fileSystem: new UnlistableFileSystem(WindowsFileSystem.Default, empty));

        Assert.Empty(outcome.Unlisted);
        Assert.True(outcome.RootRemoved);
    }

    /// <summary>
    /// The issue's first case, with Windows' own refusal: a clear that took the rest says it left the
    /// folder it could not look inside, rather than a bare "Cleared."
    /// </summary>
    [Fact]
    public async Task AClearNamesAFolderInsideItThatWouldNotList()
    {
        var scratch = _temp.CreateDirectory("scratch");
        var free = _temp.CreateFile(1024, "scratch", "abandoned.tmp");
        var locked = _temp.CreateDirectory("scratch", "installer");
        var inside = _temp.CreateFile(2048, "scratch", "installer", "setup.log");

        CleanupResult result;
        using (new DeniedDirectory(locked))
        {
            result = await Execute(new ClearDirectoryStep(scratch, "Scratch files"));
        }

        var step = Assert.Single(result.Steps);

        Assert.True(step.Succeeded);
        Assert.Equal(1, step.Unlisted);
        Assert.Equal(1, result.UnlistedCount);
        Assert.Equal(
            "Cleared, 1 folder(s) left in place because Windows would not let Deguffer look inside them.",
            step.Message);
        Assert.False(File.Exists(free));
        Assert.True(File.Exists(inside), "a file in a folder the clear could not list was removed");
        Assert.True(Directory.Exists(scratch));
    }

    /// <summary>
    /// The issue's second case: a clear whose own root will not list took nothing and saw nothing, so
    /// it fails and says why, never "It held nothing to clear."
    /// </summary>
    [Fact]
    public async Task AClearWhoseRootWouldNotListSaysSoAndFails()
    {
        var scratch = _temp.CreateDirectory("scratch");
        var inside = _temp.CreateFile(2048, "scratch", "setup.log");

        CleanupResult result;
        using (new DeniedDirectory(scratch))
        {
            result = await Execute(new ClearDirectoryStep(scratch, "Scratch files"));
        }

        var step = Assert.Single(result.Steps);

        Assert.False(step.Succeeded);
        Assert.Equal(1, step.Unlisted);
        Assert.Equal("Nothing was removed: Windows would not let Deguffer look inside 1 folder(s).", step.Message);
        Assert.True(File.Exists(inside), "a file in a folder the clear could not list was removed");
    }

    /// <summary>
    /// A clear whose root Windows will not even describe is no clear of an empty folder either: nobody
    /// can say what it holds.
    /// </summary>
    [Fact]
    public async Task AClearWhoseRootWouldNotBeDescribedSaysSoAndFails()
    {
        var scratch = _temp.CreateDirectory("tool", "scratch");
        var inside = _temp.CreateFile(2048, "tool", "scratch", "setup.log");

        CleanupResult result;
        using (DeniedDirectory.WithUnreadableAttributes(scratch))
        {
            result = await Execute(new ClearDirectoryStep(scratch, "Scratch files"));
        }

        var step = Assert.Single(result.Steps);

        Assert.False(step.Succeeded);
        Assert.Equal(1, step.Unlisted);
        Assert.Equal("Nothing was removed: Windows would not let Deguffer look inside 1 folder(s).", step.Message);
        Assert.True(File.Exists(inside), "a file in a folder the clear could not describe was removed");
    }

    /// <summary>
    /// The same root under a deletion. Windows refuses the folder as not empty, which reads as held up
    /// by something the reader was told about, and nothing was.
    /// </summary>
    [Fact]
    public async Task ADeletionWhoseRootWouldNotListSaysSoAndFails()
    {
        var cache = _temp.CreateDirectory("cache");
        var inside = _temp.CreateFile(2048, "cache", "blob.bin");

        CleanupResult result;
        using (new DeniedDirectory(cache))
        {
            result = await Execute(new DeleteDirectoryStep(cache, "A cache"));
        }

        var step = Assert.Single(result.Steps);

        Assert.False(step.Succeeded);
        Assert.Equal(1, step.Unlisted);
        Assert.Equal("Nothing was removed: Windows would not let Deguffer look inside 1 folder(s).", step.Message);
        Assert.True(File.Exists(inside), "a file in a folder the deletion could not list was removed");
        Assert.True(Directory.Exists(cache));
    }

    /// <summary>
    /// Explore's deletion of a folder that will not list removed nothing, so it says why rather than
    /// calling the folder partly deleted.
    /// </summary>
    [Fact]
    public async Task ExploreSaysNothingWasRemovedFromAFolderThatWouldNotList()
    {
        var system = new FakeSystemDirectories(_temp.Path);
        var environment = new FakeUserEnvironment(_temp.Path);
        var policy = new ExploreActionPolicy(ProtectedRegions.For(system, environment), [], new FakeVolumeInventory());

        var folder = _temp.CreateDirectory("profile", "Downloads", "junk");
        var inside = _temp.CreateFile(32, "profile", "Downloads", "junk", "a.bin");

        ExploreRemovalReport report;
        using (new DeniedDirectory(folder))
        {
            report = await ExploreRemover.RemoveAsync(
                [new ExploreItem(folder, IsDirectory: true, Bytes: 32, LowerBound: false)],
                ExploreRemovalMode.Permanent,
                policy);
        }

        Assert.Equal(
            "Nothing was removed: Windows would not let Deguffer look inside 1 folder(s).",
            Assert.Single(report.Refused).Message);
        Assert.True(File.Exists(inside));
    }
}
