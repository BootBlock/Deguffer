using System.Text.Json;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The light the map shines under the pointer: off adds nothing at all, each strength adds more than
/// the one before it, and a file written before the choice existed keeps the light the map always had.
/// </summary>
public sealed class MapPointerLightTests
{
    /// <summary>A light the reader turned off adds nothing, whatever strength it was left at.</summary>
    [Theory]
    [InlineData(MapLightStrength.Soft)]
    [InlineData(MapLightStrength.Standard)]
    [InlineData(MapLightStrength.Bright)]
    public void ALightTurnedOffAddsNothing(MapLightStrength strength) =>
        Assert.Equal(0, new MapPointerLight(Shown: false, strength).Amount);

    /// <summary>Standard is the strength the light had before it could be chosen, and the others sit either side of it.</summary>
    [Fact]
    public void EachStrengthAddsMoreThanTheOneBeforeIt()
    {
        var soft = new MapPointerLight(Strength: MapLightStrength.Soft).Amount;
        var standard = new MapPointerLight(Strength: MapLightStrength.Standard).Amount;
        var bright = new MapPointerLight(Strength: MapLightStrength.Bright).Amount;

        Assert.Equal(PointerLight.StandardStrength, standard);
        Assert.Equal(PointerLight.StandardStrength, MapPointerLight.Default.Amount);
        Assert.True(soft > 0);
        Assert.True(soft < standard);
        Assert.True(bright > standard);

        // Short of doubling a colour, which would wash every pale shape under the pointer out to white.
        Assert.True(bright < 1);
    }

    /// <summary>The light the map shines carries the strength it was given, to the shader that adds it.</summary>
    [Fact]
    public void ThePointersLightCarriesTheStrengthItWasGiven() =>
        Assert.Equal(0.2f, PointerLight.At(10, 10, 1, 0.2f).Strength);

    /// <summary>Every file on an upgraded machine was written before the light could be chosen. It reads with the light on, at the old strength.</summary>
    [Fact]
    public void AFileFromBeforeTheChoiceReadsWithTheLightOn() =>
        Assert.Equal(MapPointerLight.Default, LoadWritten("""{ "Theme": "Dark" }""").MapPointerLight);

    /// <summary>A light the file states only part of takes the default for the rest: a missing "Shown" read as false would put every light out.</summary>
    [Fact]
    public void ALightTheFileStatesInPartTakesTheDefaultsForTheRest() =>
        Assert.Equal(
            new MapPointerLight(Shown: true, MapLightStrength.Bright),
            LoadWritten("""{ "MapPointerLight": { "Strength": "Bright" } }""").MapPointerLight);

    /// <summary>A hand-edited null where the light belongs is a corrupt file, as one where the map's style belongs is.</summary>
    [Fact]
    public void ANullWhereTheLightBelongsIsACorruptFile() =>
        Assert.Equal(AppPreferences.Default, LoadWritten("""{ "Theme": "Dark", "MapPointerLight": null }"""));

    /// <summary>A strength no member names, from a hand-edited number, is read as the default's rather than as no light.</summary>
    [Fact]
    public void AStrengthNoMemberNamesIsReadAsTheDefault() =>
        Assert.Equal(MapPointerLight.Default, new MapPointerLight(Strength: (MapLightStrength)9).Defined());

    /// <summary>The file holds the two choices and nothing derived from them, which a hand-editor would take for a third.</summary>
    [Fact]
    public void TheFileHoldsOnlyTheChoices()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var store = new PreferenceStore(environment);

        Assert.True(store.Save(AppPreferences.Default with { MapPointerLight = new MapPointerLight(Shown: false, MapLightStrength.Soft) }));

        using var written = JsonDocument.Parse(File.ReadAllText(Path.Combine(environment.LocalAppData, "Deguffer", "preferences.json")));

        Assert.Equal(
            ["Shown", "Strength"],
            written.RootElement.GetProperty("MapPointerLight").EnumerateObject().Select(property => property.Name));
        Assert.Equal(new MapPointerLight(Shown: false, MapLightStrength.Soft), store.Load().MapPointerLight);
    }

    private static AppPreferences LoadWritten(string json)
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var directory = Directory.CreateDirectory(Path.Combine(environment.LocalAppData, "Deguffer"));

        File.WriteAllText(Path.Combine(directory.FullName, "preferences.json"), json);

        return new PreferenceStore(environment).Load();
    }
}
