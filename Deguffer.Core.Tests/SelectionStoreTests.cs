using Deguffer.Core.Configuration;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The remembered selection decides what a later scan offers pre-ticked, so the damaged cases are
/// the ones worth the test. Every one of them has to land on remembering nothing, which is the
/// shipped behaviour of tier defaults rather than a guess at what the user meant.
/// </summary>
public sealed class SelectionStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public SelectionStoreTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private SelectionStore CreateStore() => new(_environment);

    private string StoreFile => Path.Combine(_environment.LocalAppData, "Deguffer", "selection.json");

    [Fact]
    public void ReadsBackBothLevelsOfWhatWasSaved()
    {
        var store = CreateStore();

        Assert.True(store.Save(new Dictionary<string, RememberedSelection>
        {
            ["npm"] = new(IsSelected: false, new Dictionary<string, bool>
            {
                [@"C:\Users\testuser\AppData\Local\npm-cache\_cacache"] = false,
            }),
            ["node-modules"] = new(IsSelected: true, new Dictionary<string, bool>
            {
                [@"C:\Users\testuser\src\alpha\node_modules"] = true,
                [@"C:\Users\testuser\src\beta\node_modules"] = false,
            }),
        }));

        var loaded = CreateStore().Load();

        // The row and its steps disagree deliberately in both entries. A round trip that only
        // carried the row would satisfy an assertion on either one on its own.
        Assert.False(loaded["npm"].IsSelected);
        Assert.False(loaded["npm"].Steps[@"C:\Users\testuser\AppData\Local\npm-cache\_cacache"]);

        Assert.True(loaded["node-modules"].IsSelected);
        Assert.True(loaded["node-modules"].Steps[@"C:\Users\testuser\src\alpha\node_modules"]);
        Assert.False(loaded["node-modules"].Steps[@"C:\Users\testuser\src\beta\node_modules"]);
    }

    [Fact]
    public void RemembersNothingOnFirstRun()
    {
        Assert.Empty(CreateStore().Load());
    }

    /// <summary>
    /// A file that cannot be read is not a decision the user made, so it must not be treated as
    /// one in either direction. Falling through to the §3 defaults is the only honest answer.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"npm\": {\"IsSelected\": true}")]
    [InlineData("[\"npm\"]")]
    [InlineData("null")]
    public void RemembersNothingFromACorruptFile(string content)
    {
        CreateStore().Save(new Dictionary<string, RememberedSelection>
        {
            ["npm"] = new(IsSelected: true, new Dictionary<string, bool>()),
        });

        File.WriteAllText(StoreFile, content);

        Assert.Empty(CreateStore().Load());
    }

    /// <summary>
    /// A hand-edited entry with no step map at all is well-formed JSON, so it reaches the record
    /// rather than the catch above, and it arrives as a null the rest of the code would dereference.
    /// </summary>
    [Fact]
    public void ReadsAnEntryThatNamesNoSteps()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
        File.WriteAllText(StoreFile, "{\"npm\": {\"IsSelected\": false}}");

        var loaded = CreateStore().Load();

        Assert.False(loaded["npm"].IsSelected);
        Assert.Null(loaded["npm"].Steps);
    }
}
