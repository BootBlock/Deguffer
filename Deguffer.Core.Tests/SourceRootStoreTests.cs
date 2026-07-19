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

        Assert.True(store.Save([@"C:\Users\testuser\src", @"D:\work"]));
        Assert.Equal([@"C:\Users\testuser\src", @"D:\work"], CreateStore().Load());
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
        store.Save([@"C:\Users\testuser\src"]);

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
        Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
        File.WriteAllText(
            StoreFile,
            "[\"C:\\\\Users\\\\testuser\\\\src\", \"..\\\\escape\", \"relative\", \"\", \"D:\\\\work\"]");

        Assert.Equal([@"C:\Users\testuser\src", @"D:\work"], CreateStore().Load());
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

        Assert.True(store.Save([@"C:\Users\testuser\src", "relative", @"c:\users\testuser\SRC"], out var stored));

        Assert.Equal([@"C:\Users\testuser\src"], stored);
        Assert.Equal(stored, CreateStore().Load());
    }

    [Fact]
    public void DoesNotStoreTheSameRootTwice()
    {
        CreateStore().Save([@"C:\Users\testuser\src", @"c:\users\testuser\SRC"]);

        Assert.Single(CreateStore().Load());
    }

    /// <summary>
    /// The store writes into <c>%LOCALAPPDATA%\Deguffer</c> and must create it, rather than failing
    /// the first save on a profile where nothing has been written yet.
    /// </summary>
    [Fact]
    public void CreatesItsDirectoryOnFirstSave()
    {
        Assert.True(CreateStore().Save([@"C:\Users\testuser\src"]));
        Assert.True(File.Exists(StoreFile));
    }
}
