using Deguffer.Core.Configuration;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The rail's setting was a flag before it had three values. A reader who folded the rail on an
/// earlier version keeps it folded, and a file written now carries only the new setting.
/// </summary>
public sealed class RailChoiceStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public RailChoiceStoreTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string File => Path.Combine(_environment.LocalAppData, "Deguffer", "preferences.json");

    [Fact]
    public void ARailFoldedBeforeTheChoiceExistedStaysFolded()
    {
        var loaded = LoadWritten("""{ "Theme": "Dark", "NavigationCollapsed": true }""");

        // The file parsed, rather than falling through to the defaults wholesale.
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(RailChoice.Collapsed, loaded.Rail);
    }

    /// <summary>False was the shipped default as well as a choice, so it cannot say the reader wanted the labels at every width.</summary>
    [Fact]
    public void ARailLeftOpenBeforeTheChoiceExistedFollowsTheWidth()
    {
        var loaded = LoadWritten("""{ "Theme": "Dark", "NavigationCollapsed": false }""");

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(RailChoice.Automatic, loaded.Rail);
    }

    [Fact]
    public void AFileWithNoRailSettingFollowsTheWidth() =>
        Assert.Equal(RailChoice.Automatic, LoadWritten("""{ "Theme": "Dark" }""").Rail);

    /// <summary>The old key is read and never written, so it cannot outvote the new one on the next launch.</summary>
    [Fact]
    public void TheChoiceIsStoredByNameAndTheOldFlagIsNotWritten()
    {
        var store = new PreferenceStore(_environment);

        Assert.True(store.Save(AppPreferences.Default with { Rail = RailChoice.Open }));

        var json = System.IO.File.ReadAllText(File);
        Assert.Contains("\"Rail\": \"Open\"", json);
        Assert.DoesNotContain("NavigationCollapsed", json);
        Assert.Equal(RailChoice.Open, store.Load().Rail);
    }

    /// <summary>A record's equality must not see the old flag, or two equal settings would differ by a key no file carries.</summary>
    [Fact]
    public void TheOldFlagIsNotPartOfTheSettingsValue() =>
        Assert.Equal(AppPreferences.Default, LoadWritten("""{ "NavigationCollapsed": false }"""));

    private AppPreferences LoadWritten(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, json);

        return new PreferenceStore(_environment).Load();
    }
}
