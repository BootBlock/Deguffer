using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The keep list on disk. A damaged file keeps nothing for the session and is left exactly as it was,
/// and the one property worth the most is that the keep list and the remembered selection never read
/// or write each other's file.
/// </summary>
public sealed class KeepStoreTests : IDisposable
{
    private static readonly KeptItem Chromium =
        new("playwright", "Playwright browsers", new ItemIdentity("chromium-1228", "chromium-1228"));

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public KeepStoreTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string Folder => Path.Combine(_environment.LocalAppData, "Deguffer");

    private string StoreFile => Path.Combine(Folder, "keep.json");

    [Fact]
    public void ReadsBackWhatWasSaved()
    {
        var release = new KeptItem(
            "azure-functions-tools", "Azure Functions Core Tools releases", new ItemIdentity("4.18.1", "Azure Functions Core Tools 4.18.1"));

        Assert.True(new KeepStore(_environment).Save(new KeepList([Chromium, release])));

        var loaded = new KeepStore(_environment).Load();

        Assert.Equal([Chromium, release], loaded.Items);
        Assert.Contains("chromium-1228", loaded.KeysFor("playwright"));
        Assert.Contains("4.18.1", loaded.KeysFor("azure-functions-tools"));
    }

    [Fact]
    public void KeepsNothingOnFirstRun()
    {
        Assert.Empty(new KeepStore(_environment).Load().Items);
    }

    /// <summary>
    /// A file that cannot be read keeps nothing this session. Keeping nothing offers what was kept, and
    /// the preview and §7's confirmations still stand in front of that; guessing at what the file meant
    /// does not. The file itself may still hold every entry, which is why it is never written over:
    /// see <see cref="AFileItCouldNotReadIsNeverWrittenOver"/>.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"playwright\": [\"chromium-1228\"]}")]
    [InlineData("[{\"ProviderId\": \"playwright\"")]
    [InlineData("null")]
    public void KeepsNothingFromACorruptFile(string content)
    {
        new KeepStore(_environment).Save(new KeepList([Chromium]));
        File.WriteAllText(StoreFile, content);

        Assert.Empty(new KeepStore(_environment).Load().Items);
    }

    /// <summary>
    /// Well-formed JSON with a bad line in it costs that line and nothing else. A missing name is not
    /// a bad line: the key is what protects the item, and the name only describes it.
    /// </summary>
    [Fact]
    public void DropsAnEntryItCannotMatchAndKeepsTheRest()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(StoreFile, """
            [
              null,
              { "ProviderId": "playwright", "ProviderName": "Playwright browsers" },
              { "ProviderId": "playwright", "Item": { "Key": "   ", "Name": "blank" } },
              { "ProviderId": "", "Item": { "Key": "orphan", "Name": "orphan" } },
              { "ProviderId": "playwright", "Item": { "Key": "firefox-1532" } }
            ]
            """);

        var item = Assert.Single(new KeepStore(_environment).Load().Items);

        Assert.Equal("firefox-1532", item.Item.Key);
        Assert.Equal("firefox-1532", item.Item.Name);
        Assert.Equal("playwright", item.ProviderName);
    }

    /// <summary>
    /// The two files have opposite safety polarity, so a keep list never goes where a selection is
    /// remembered. Saving one leaves the remembered selection exactly as it was, byte for byte.
    /// </summary>
    [Fact]
    public void SavingAKeepListNeverTouchesTheRememberedSelection()
    {
        new SelectionStore(_environment).Save(new Dictionary<string, RememberedSelection>
        {
            ["playwright"] = new(IsSelected: false, new Dictionary<string, bool> { ["chromium-1228"] = false }),
        });

        var selectionFile = Path.Combine(Folder, "selection.json");
        var before = File.ReadAllBytes(selectionFile);

        Assert.True(new KeepStore(_environment).Save(new KeepList([Chromium])));

        Assert.Equal(before, File.ReadAllBytes(selectionFile));
        Assert.True(File.Exists(StoreFile));
    }

    /// <summary>
    /// The reading half of the same property. A remembered selection on disk is not a keep list that
    /// could not be read: it is not the keep list's file at all, so the store keeps nothing from it and
    /// has no reason to refuse the first save.
    /// </summary>
    [Fact]
    public void TheKeepListNeverReadsTheRememberedSelection()
    {
        new SelectionStore(_environment).Save(new Dictionary<string, RememberedSelection>
        {
            ["playwright"] = new(IsSelected: true, new Dictionary<string, bool> { ["chromium-1228"] = true }),
        });

        var store = new KeepStore(_environment);

        Assert.Empty(store.Load().Items);
        Assert.False(store.RefusesToSave);
    }

    /// <summary>
    /// A file the store could not make sense of may still hold every entry the user kept, so the next
    /// keep must not be saved over it. The session keeps nothing, which is the narrow failure; the file
    /// keeps everything, which is what makes that failure temporary.
    /// </summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("[{\"ProviderId\": \"playwright\"")]
    [InlineData("{\"playwright\": [\"chromium-1228\"]}")]
    public void AFileItCouldNotReadIsNeverWrittenOver(string content)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(StoreFile, content);

        var store = new KeepStore(_environment);

        Assert.Empty(store.Load().Items);
        Assert.True(store.RefusesToSave);
        Assert.False(store.Save(new KeepList([Chromium])));
        Assert.Equal(content, File.ReadAllText(StoreFile));
    }

    /// <summary>
    /// A first run has no file to lose, so it is the one failed read that must not refuse: refusing
    /// there would stop the keep list ever being saved on a new machine.
    /// </summary>
    [Fact]
    public void AMissingFileIsNoReasonToRefuseASave()
    {
        var store = new KeepStore(_environment);

        Assert.Empty(store.Load().Items);
        Assert.False(store.RefusesToSave);
        Assert.True(store.Save(new KeepList([Chromium])));
        Assert.Equal([Chromium], new KeepStore(_environment).Load().Items);
    }

    /// <summary>
    /// The ordinary way a good file becomes unreadable: something else has it open as Deguffer starts.
    /// Every entry is still in it, and the next keep must not replace them with one.
    /// </summary>
    [Fact]
    public void AFileHeldOpenAtStartupIsNeverWrittenOver()
    {
        Assert.True(new KeepStore(_environment).Save(new KeepList([Chromium])));
        var saved = File.ReadAllText(StoreFile);

        var store = new KeepStore(_environment);

        using (new FileStream(StoreFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(store.Load().Items);
        }

        var another = new KeptItem("azure-functions-tools", "Azure Functions Core Tools releases", new ItemIdentity("4.18.1", "4.18.1"));

        Assert.False(store.Save(new KeepList([another])));
        Assert.Equal(saved, File.ReadAllText(StoreFile));
    }

    /// <summary>
    /// Damage costs only what it touches. A key that cannot be read matches nothing, so that entry goes.
    /// Names of the wrong type only describe an item, so that entry stays kept, under its provider's id
    /// and its key.
    /// </summary>
    [Fact]
    public void AFieldOfTheWrongTypeCostsOnlyWhatItTouches()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(StoreFile, """
            [
              { "ProviderId": "playwright", "ProviderName": "Playwright browsers", "Item": { "Key": 1228, "Name": "chromium" } },
              { "ProviderId": "playwright", "ProviderName": 5, "Item": { "Key": "firefox-1532", "Name": ["firefox"] } },
              "not an entry"
            ]
            """);

        var store = new KeepStore(_environment);
        var item = Assert.Single(store.Load().Items);

        Assert.False(store.RefusesToSave);
        Assert.Equal("firefox-1532", item.Item.Key);
        Assert.Equal("firefox-1532", item.Item.Name);
        Assert.Equal("playwright", item.ProviderName);
    }
}
