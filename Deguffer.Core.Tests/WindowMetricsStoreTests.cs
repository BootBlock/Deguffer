using Deguffer.Core.Configuration;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The placement file is written on every close and read on every launch, so the damaged cases are
/// the ones worth the test. Each has to land on remembering nothing, which is the framework-default
/// placement the app opened with before it remembered anything.
/// </summary>
public sealed class WindowMetricsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public WindowMetricsStoreTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private WindowMetricsStore CreateStore() => new(_environment);

    private string StoreFile => Path.Combine(_environment.LocalAppData, "Deguffer", "window.json");

    /// <summary>Put <paramref name="json"/> in the store's place, as a hand edit would.</summary>
    private void HandEdit(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
        File.WriteAllText(StoreFile, json);
    }

    /// <summary>
    /// All five values, and the maximised flag set against a restored rectangle that is plainly not
    /// a maximised one. A round trip that dropped either half would still satisfy an assertion on
    /// the other.
    /// </summary>
    [Fact]
    public void ReadsBackWhatWasSaved()
    {
        Assert.True(CreateStore().Save(new WindowMetrics(new WindowBounds(412, 96, 1000, 700), IsMaximized: true)));

        var loaded = CreateStore().Load();

        Assert.NotNull(loaded);
        Assert.Equal(new WindowBounds(412, 96, 1000, 700), loaded.Bounds);
        Assert.True(loaded.IsMaximized);
    }

    [Fact]
    public void RemembersNothingOnFirstRun()
    {
        Assert.Null(CreateStore().Load());
    }

    [Fact]
    public void CreatesItsDirectoryOnFirstSave()
    {
        Assert.True(CreateStore().Save(new WindowMetrics(new WindowBounds(0, 0, 1000, 700), IsMaximized: false)));
        Assert.True(File.Exists(StoreFile));
    }

    [Fact]
    public void RemembersNothingWhenTheFileIsCorrupt()
    {
        CreateStore().Save(new WindowMetrics(new WindowBounds(412, 96, 1000, 700), IsMaximized: false));
        HandEdit("{ not json");

        Assert.Null(CreateStore().Load());
    }

    /// <summary>
    /// The literal <c>null</c> document, which is well-formed JSON and so never reaches the catch.
    /// Read straight out it is a null record, and the window would open by dereferencing it.
    /// </summary>
    [Fact]
    public void RemembersNothingWhenTheDocumentIsNull()
    {
        CreateStore().Save(new WindowMetrics(new WindowBounds(412, 96, 1000, 700), IsMaximized: false));
        HandEdit("null");

        Assert.Null(CreateStore().Load());
    }

    /// <summary>
    /// <c>{}</c> is well-formed too, and gives a record whose every number is zero. A window of no
    /// size is not a small window, and clamping it up to the floor would open the application
    /// somewhere the user never left it — in the top-left corner of the primary display, at the
    /// smallest size the layout allows. Nothing remembered is the honest answer.
    /// </summary>
    [Fact]
    public void RemembersNothingWhenTheStoredRectangleIsNotAWindow()
    {
        HandEdit("{}");

        Assert.Null(CreateStore().Load());
    }

    /// <summary>A negative extent, which is the same non-window arrived at from the other side.</summary>
    [Fact]
    public void RemembersNothingWhenTheStoredExtentIsNegative()
    {
        HandEdit("""{ "Bounds": { "X": 412, "Y": 96, "Width": 1000, "Height": -700 }, "IsMaximized": false }""");

        Assert.Null(CreateStore().Load());
    }

    /// <summary>
    /// A placement off every display is stored as it stands rather than rejected here. Whether it
    /// still fits is a question about the desktop the window is opening onto, which this file knows
    /// nothing about — <see cref="WindowBounds.Within"/> answers it at that point.
    /// </summary>
    [Fact]
    public void KeepsAPlacementThatNoDisplayReaches()
    {
        CreateStore().Save(new WindowMetrics(new WindowBounds(-9000, -9000, 1000, 700), IsMaximized: false));

        Assert.Equal(new WindowBounds(-9000, -9000, 1000, 700), CreateStore().Load()?.Bounds);
    }
}
