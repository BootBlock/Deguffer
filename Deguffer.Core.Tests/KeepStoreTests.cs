using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The keep list on disk. The damaged cases land on keeping nothing, and the one property worth the
/// most is that this file and the selection memory can never be read as each other.
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
    /// A file that cannot be read is not a list the user made. Keeping nothing offers what was kept,
    /// and the preview and §7's confirmations still stand in front of that. Guessing does not.
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
    /// The two files have opposite safety polarity, so neither may ever be read as the other. Saving
    /// a keep list writes no selection, and a selection file sitting where the keep list is expected
    /// keeps nothing — and above all ticks nothing.
    /// </summary>
    [Fact]
    public void TheKeepListAndTheSelectionMemoryAreNeverReadAsEachOther()
    {
        Assert.True(new KeepStore(_environment).Save(new KeepList([Chromium])));

        Assert.False(File.Exists(Path.Combine(Folder, "selection.json")));
        Assert.False(new SelectionMemory(new SelectionStore(_environment).Load())
            .RowStartsSelected("playwright", SafetyTier.RegenerableWithCost, byDefault: false));

        // A remembered selection copied over the keep list: well-formed JSON of the wrong shape.
        new SelectionStore(_environment).Save(new Dictionary<string, RememberedSelection>
        {
            ["playwright"] = new(IsSelected: true, new Dictionary<string, bool> { ["chromium-1228"] = true }),
        });
        File.Copy(Path.Combine(Folder, "selection.json"), StoreFile, overwrite: true);

        Assert.Empty(new KeepStore(_environment).Load().Items);
    }
}
