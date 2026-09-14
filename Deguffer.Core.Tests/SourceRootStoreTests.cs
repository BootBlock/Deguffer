using Deguffer.Core.Configuration;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Approved roots are the first stored setting that changes what Deguffer will delete, so the
/// failure direction matters in a way it does not for a theme. Every degraded read has to narrow
/// scope, never widen it.
/// </summary>
public sealed class SourceRootStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public SourceRootStoreTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private SourceRootStore CreateStore() => new(_environment);

    private string StoreFile => Path.Combine(_environment.LocalAppData, "Deguffer", "source-roots.json");

    [Fact]
    public void ReadsBackWhatWasSaved()
    {
        var store = CreateStore();

        Assert.True(store.Save([new SourceRoot(@"C:\Users\testuser\src"), new SourceRoot(@"D:\work")]));
        Assert.Equal(
            [new SourceRoot(@"C:\Users\testuser\src"), new SourceRoot(@"D:\work")],
            CreateStore().Load());
    }

    [Fact]
    public void HasNoApprovedRootsOnFirstRun()
    {
        Assert.Empty(CreateStore().Load());
    }

    /// <summary>
    /// The asymmetry that matters. A corrupt file must yield nothing — falling back to anything
    /// plausible would be approving a folder the user never chose.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[\"C:\\\\Users\\\\testuser\\\\src\"")]
    [InlineData("{\"roots\": [\"C:\\\\Users\\\\testuser\\\\src\"]}")]
    [InlineData("null")]
    public void YieldsNoRootsFromACorruptFileRatherThanAWiderScope(string content)
    {
        var store = CreateStore();
        store.Save([new SourceRoot(@"C:\Users\testuser\src")]);

        File.WriteAllText(StoreFile, content);

        Assert.Empty(CreateStore().Load());
    }

    /// <summary>
    /// A relative path would resolve against whatever directory the process happens to be running
    /// in, which is not a folder anyone approved. One bad entry costs that entry, not the file.
    /// </summary>
    [Fact]
    public void DropsEntriesThatAreNotAbsolutePathsAndKeepsTheRest()
    {
        Write("[\"C:\\\\Users\\\\testuser\\\\src\", \"..\\\\escape\", \"relative\", \"\", \"D:\\\\work\"]");

        Assert.Equal([Root(@"C:\Users\testuser\src"), Root(@"D:\work")], CreateStore().Load());
    }

    /// <summary>
    /// An entry whose shape makes no sense costs that entry, on the same reasoning as a relative
    /// path: a number where the folder should be is one line of a hand-edited file, not a reason to
    /// drop every approval beside it.
    /// </summary>
    [Theory]
    [InlineData("{\"path\": 5}")]
    [InlineData("{\"path\": null}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("7")]
    [InlineData("true")]
    public void DropsAnEntryOfAnUnusableShapeAndKeepsTheRest(string entry)
    {
        Write($"[{entry}, \"D:\\\\work\"]");

        Assert.Equal([Root(@"D:\work")], CreateStore().Load());
    }

    /// <summary>
    /// What reached disk is reported back, because it is not always what was asked for. A caller
    /// keeping its own copy has to adopt this: mirroring the requested list instead leaves the
    /// Settings page showing a folder that is not in the file and will never be searched.
    /// </summary>
    [Fact]
    public void ReportsWhatWasActuallyStoredRatherThanWhatWasAsked()
    {
        var store = CreateStore();

        Assert.True(
            store.Save(
                [Root(@"C:\Users\testuser\src"), Root("relative"), Root(@"c:\users\testuser\SRC")],
                out var stored));

        Assert.Equal([Root(@"C:\Users\testuser\src")], stored);
        Assert.Equal(stored, CreateStore().Load());
    }

    [Fact]
    public void DoesNotStoreTheSameRootTwice()
    {
        CreateStore().Save([Root(@"C:\Users\testuser\src"), Root(@"c:\users\testuser\SRC")]);

        Assert.Single(CreateStore().Load());
    }

    /// <summary>
    /// The store writes into <c>%LOCALAPPDATA%\Deguffer</c> and must create it, rather than failing
    /// the first save on a profile where nothing has been written yet.
    /// </summary>
    [Fact]
    public void CreatesItsDirectoryOnFirstSave()
    {
        Assert.True(CreateStore().Save([Root(@"C:\Users\testuser\src")]));
        Assert.True(File.Exists(StoreFile));
    }

    /// <summary>
    /// A root reaches disk resolved, whatever form it arrived in.
    ///
    /// This is the only configured path in Deguffer that used to skip <c>LongPath.Configured</c>,
    /// and it matters because two routes read it. The walk resolves a root itself on the way to the
    /// extended-length form, while the volume index narrows its results by comparing strings — so a
    /// hand-edited entry with forward slashes, a trailing separator or a <c>..</c> segment would make
    /// an elevated run return an empty plan where an unelevated one found every project.
    /// </summary>
    [Theory]
    [InlineData(@"C:/Users/testuser/src")]
    [InlineData(@"C:\Users\testuser\src\")]
    [InlineData(@"C:\Users\testuser\tools\..\src")]
    public void NormalisesARootHoweverItWasWritten(string written)
    {
        Assert.True(CreateStore().Save([Root(written)], out var stored));

        Assert.Equal([Root(@"C:\Users\testuser\src")], stored);
        Assert.Equal(stored, CreateStore().Load());
    }

    /// <summary>
    /// The answer the user gave about a folder's volume survives a restart. Without it the Settings
    /// page would ask once, store nothing, and the next preview would refuse the folder again.
    /// </summary>
    [Fact]
    public void KeepsTheApprovalGivenForAFolderOnACloudMount()
    {
        CreateStore().Save(
        [
            new SourceRoot(@"V:\work", RemoteStorageApproved: true),
            new SourceRoot(@"C:\Users\testuser\src"),
        ]);

        Assert.Equal(
            [
                new SourceRoot(@"V:\work", RemoteStorageApproved: true),
                new SourceRoot(@"C:\Users\testuser\src"),
            ],
            CreateStore().Load());
    }

    /// <summary>
    /// The shape every folder approved before Deguffer read volume flags is stored in. It has to keep
    /// working — a read that dropped it would un-approve every folder on upgrade — and it has to
    /// arrive carrying no approval, because nobody was asked. Discovery then refuses such a folder if
    /// it turns out to be on a cloud mount, which costs a search rather than a download.
    /// </summary>
    [Fact]
    public void ReadsAFolderStoredAsABareStringAndGivesItNoApproval()
    {
        Write("[\"V:\\\\work\", \"C:\\\\Users\\\\testuser\\\\src\"]");

        Assert.Equal(
            [new SourceRoot(@"V:\work"), new SourceRoot(@"C:\Users\testuser\src")],
            CreateStore().Load());
    }

    /// <summary>
    /// Both shapes in one file, which is what a hand-edited file or a half-finished write leaves. The
    /// two are read side by side rather than the file being taken as one form or the other.
    /// </summary>
    [Fact]
    public void ReadsTheOldAndTheNewShapeInOneFile()
    {
        Write(
            "[\"C:\\\\Users\\\\testuser\\\\src\", "
            + "{\"path\": \"V:\\\\work\", \"remoteStorageApproved\": true}]");

        Assert.Equal(
            [new SourceRoot(@"C:\Users\testuser\src"), new SourceRoot(@"V:\work", RemoteStorageApproved: true)],
            CreateStore().Load());
    }

    /// <summary>
    /// Anything but <c>true</c> for the approval is no approval, including the value being missing or
    /// written as a string. This is the one flag in Deguffer whose wrong reading spends a download of
    /// the user's cloud storage, so every unclear answer is the narrow one.
    /// </summary>
    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    public void TreatsAnythingButTrueAsNoApproval(string written)
    {
        Write($"[{{\"path\": \"V:\\\\work\", \"remoteStorageApproved\": {written}}}]");

        Assert.Equal([new SourceRoot(@"V:\work")], CreateStore().Load());
    }

    /// <summary>
    /// The first entry for a folder wins where the file names it twice. An approval and a refusal of
    /// the same folder are indistinguishable in the file from the folder written twice, so the rule is
    /// stated rather than left to whichever entry the deduplication happens to keep.
    /// </summary>
    [Fact]
    public void KeepsTheFirstEntryWhereAFolderIsNamedTwice()
    {
        Write(
            "[{\"path\": \"V:\\\\work\", \"remoteStorageApproved\": false}, "
            + "{\"path\": \"v:\\\\WORK\", \"remoteStorageApproved\": true}]");

        Assert.Equal([new SourceRoot(@"V:\work")], CreateStore().Load());
    }

    private static SourceRoot Root(string path) => new(path);

    private void Write(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
        File.WriteAllText(StoreFile, content);
    }
}
