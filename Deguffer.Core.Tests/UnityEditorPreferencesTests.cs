using System.Text;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Reading the Unity Editor's preferences from the names Unity gives their values: the preference's
/// name, <c>_h</c>, and a hash in decimal.
/// </summary>
public sealed class UnityEditorPreferencesTests : IDisposable
{
    private const string Key = UnityEditorPreferences.Key;

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public UnityEditorPreferencesTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private UnityEditorPreferences Read() => UnityEditorPreferences.Read(_environment);

    /// <summary>Unity writes text as UTF-8 bytes ending in a NUL, which is not part of the text.</summary>
    [Fact]
    public void ReadsTextStoredAsBytesWithoutItsTerminator()
    {
        _environment.WithRegistryBytes(Key, "GICacheFolder_h3275139823", Encoding.UTF8.GetBytes("D:/Lighting/Caché\0"));

        Assert.Equal("D:/Lighting/Caché", Read().Text("GICacheFolder"));
    }

    [Fact]
    public void ReadsTextStoredAsAString()
    {
        _environment.WithRegistryValue(Key, "GICacheFolder_h3275139823", "D:/Lighting");

        Assert.Equal("D:/Lighting", Read().Text("GICacheFolder"));
    }

    /// <summary>
    /// Only the preference's own value: a longer name that begins the same way, a suffix that is not a
    /// hash, and the same name in another case are other values.
    /// </summary>
    [Theory]
    [InlineData("GICacheFolderOld_h3275139823")]
    [InlineData("GICacheFolder_hx3275139823")]
    [InlineData("GICacheFolder_h")]
    [InlineData("GICacheFolder")]
    [InlineData("gicachefolder_h3275139823")]
    public void IgnoresAValueNamedForAnotherPreference(string valueName)
    {
        _environment.WithRegistryBytes(Key, valueName, Encoding.UTF8.GetBytes("D:/Lighting\0"));

        Assert.Null(Read().Text("GICacheFolder"));
    }

    /// <summary>A preference the Editor set to nothing is no folder.</summary>
    [Fact]
    public void ReadsEmptyTextAsUnset()
    {
        _environment.WithRegistryBytes(Key, "GICacheFolder_h3275139823", [0]);

        Assert.Null(Read().Text("GICacheFolder"));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void ReadsAFlagFromItsNumber(int stored, bool expected)
    {
        _environment.WithRegistryNumber(Key, "GICacheEnableCustomPath_h2093405155", stored);

        Assert.Equal(expected, Read().IsOn("GICacheEnableCustomPath"));
    }

    [Fact]
    public void AFlagNeverSetIsOff()
    {
        Assert.False(Read().IsOn("GICacheEnableCustomPath"));
    }
}
