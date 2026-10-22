using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The named looks the appearance window offers: each applies as a whole, is recognised once
/// applied, and writes nothing but the settings it sets. The two that follow other disk maps are held
/// to what those maps draw.
/// </summary>
public sealed class MapPresetTests
{
    /// <summary>The first preset is the look the map is shipped with, so choosing it puts that back.</summary>
    [Fact]
    public void TheFirstPresetIsTheShippedLook()
    {
        var shipped = MapLook.Default;

        Assert.Same(MapPreset.Standard, MapPreset.All[0]);
        Assert.True(MapPreset.Standard.Describes(shipped));
        Assert.Equal(ExploreColouring.Branch, MapPreset.Standard.Colouring);
    }

    /// <summary>
    /// No two presets draw the same look, or the window could not say which one the map is drawn as,
    /// and each is recognised in the look it makes, whatever that look started from.
    /// </summary>
    [Fact]
    public void EachPresetIsRecognisedInTheLookItMakesAndNoOther()
    {
        var started = MapLook.Default with { Treemap = ExploreScheme.Vivid, Spacing = ExploreSpacing.Spacious };

        foreach (var preset in MapPreset.All)
        {
            var look = preset.AppliedTo(started);

            Assert.Equal([preset], MapPreset.All.Where(other => other.Describes(look)));
            Assert.Equal(ExploreScheme.Vivid, look.Treemap);
        }

        Assert.Equal(MapPreset.All.Count, MapPreset.All.Select(preset => preset.Name).Distinct().Count());
    }

    /// <summary>
    /// A preset written into the preferences sets the style and the spacing and nothing else: not the
    /// schemes, not the colouring, which the Explore page writes, and not anything that governs what is
    /// deleted.
    /// </summary>
    [Fact]
    public void APresetWritesItsStyleAndSpacingAndNothingElse()
    {
        var before = AppPreferences.Default with
        {
            ConfirmBeforeCleaning = false,
            TreemapScheme = ExploreScheme.Deep,
            ExploreColours = ExploreColouring.Age,
        };

        var after = MapPreset.SpaceMonger.Into(before);

        Assert.Equal(before with { MapStyle = MapPreset.SpaceMonger.Style, TreemapSpacing = ExploreSpacing.Comfortable }, after);
    }

    /// <summary>
    /// SpaceMonger 1.4 draws flat boxes with a one-pixel bevel and a black line round every box, file
    /// and folder alike, names each folder in a band, and colours by depth.
    /// </summary>
    [Fact]
    public void SpaceMongerIsBevelledBoxesWithABlackLineColouredByDepth()
    {
        var style = MapPreset.SpaceMonger.Style;

        Assert.Equal(MapShading.Bevel, style.Shading);
        Assert.Equal((MapBorder.Solid, MapBorder.Solid), (style.BlockBorders, style.FolderBorders));
        Assert.Equal((MapBorderWidth.Thin, MapBorderTone.Dark), (style.BorderWidth, style.BorderTone));
        Assert.True(style.FolderNames);
        Assert.Equal(ExploreColouring.Depth, MapPreset.SpaceMonger.Colouring);
    }

    /// <summary>
    /// WizTree 4 draws cushions with no line round them, a narrow gap between files, folders as dark
    /// grey frames named in their bands, and files coloured by kind.
    /// </summary>
    [Fact]
    public void WizTreeIsCushionsWithNoLinesInGreyFoldersColouredByKind()
    {
        var style = MapPreset.WizTree.Style;

        Assert.Equal(MapShading.Cushion, style.Shading);
        Assert.Equal((MapBorder.None, MapBorder.None), (style.BlockBorders, style.FolderBorders));
        Assert.Equal(MapFolderFill.Grey, style.FolderFill);
        Assert.True(style.FolderNames);
        Assert.Equal(ExploreSpacing.Dense, MapPreset.WizTree.Spacing);
        Assert.Equal(ExploreColouring.Type, MapPreset.WizTree.Colouring);
    }

    /// <summary>
    /// A look a single choice away from a preset is a look of the reader's own: every part of the style
    /// is asked, not only the ones that tell the presets apart from each other.
    /// </summary>
    [Fact]
    public void ALookOneChoiceFromAPresetIsNotThatPreset()
    {
        foreach (var preset in MapPreset.All)
        {
            var look = preset.AppliedTo(MapLook.Default);
            var style = look.Style;
            var changed = new[]
            {
                style with { Shading = style.Shading == MapShading.Flat ? MapShading.Bevel : MapShading.Flat },
                style with { Relief = style.Relief == MapRelief.Subtle ? MapRelief.Strong : MapRelief.Subtle },
                style with { BlockBorders = style.BlockBorders == MapBorder.Faint ? MapBorder.Medium : MapBorder.Faint },
                style with { FolderBorders = style.FolderBorders == MapBorder.Faint ? MapBorder.Medium : MapBorder.Faint },
                style with { BorderWidth = style.BorderWidth == MapBorderWidth.Thin ? MapBorderWidth.Thick : MapBorderWidth.Thin },
                style with { BorderTone = style.BorderTone == MapBorderTone.Dark ? MapBorderTone.Light : MapBorderTone.Dark },
                style with { FolderFill = style.FolderFill == MapFolderFill.Grey ? MapFolderFill.Coloured : MapFolderFill.Grey },
                style with { FolderNames = !style.FolderNames },
                style with { ShapeNames = !style.ShapeNames },
            };

            Assert.All(changed, other => Assert.False(preset.Describes(look with { Style = other }), $"{preset.Name} described {other}"));
            Assert.False(preset.Describes(look with { Spacing = look.Spacing == ExploreSpacing.Spacious ? ExploreSpacing.Dense : ExploreSpacing.Spacious }));
        }
    }

    /// <summary>
    /// The preview's sample is coloured as the page's map is where the sample can say it: by depth and
    /// by kind, which are facts about its shape and names. Age and growth, which it has none of, fall
    /// back to its branches rather than one grey.
    /// </summary>
    [Fact]
    public void ThePreviewIsColouredAsThePageWhereTheSampleCanSayIt()
    {
        Assert.Same(ShapeColours.ByDepth, MapSample.ColoursFor(ExploreColouring.Depth, ExploreScheme.Soft));
        Assert.Same(
            ShapeColours.ByBranch(ExploreScheme.Soft),
            MapSample.ColoursFor(ExploreColouring.Age, ExploreScheme.Soft));
        Assert.Same(
            ShapeColours.ByBranch(ExploreScheme.Deep),
            MapSample.ColoursFor(ExploreColouring.Growth, ExploreScheme.Deep));

        var types = MapSample.ColoursFor(ExploreColouring.Type, ExploreScheme.Standard);
        var drawing = ExploreSurface.Create(
            MapSample.Tree, MapSample.Tree.RootNode, ExploreView.Treemap, 320, 160, 1, 1,
            types, ExploreSpacing.Comfortable, VolumeSpace.None);
        var colours = drawing.MorphShapes(drawing.NodesDrawn.ToHashSet()).Select(shape => shape.Colour).ToHashSet();

        Assert.Contains(TypePalette.For(FileCategory.Images), colours);
        Assert.DoesNotContain(TypePalette.Unmeasured, colours);
    }

    /// <summary>Every preset states a value each enum names, as a hand-edited file may not.</summary>
    [Fact]
    public void EveryPresetIsAStyleTheMapCanDraw() =>
        Assert.All(MapPreset.All, preset => Assert.Equal(preset.Style, preset.Style.Defined()));
}
