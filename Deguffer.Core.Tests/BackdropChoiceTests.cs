using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which material a window is drawn on (§6.5): Acrylic where it is asked for and can be drawn, Mica
/// where only Mica can be, and the window's own ground where the reader or high contrast says no.
/// </summary>
public sealed class BackdropChoiceTests
{
    [Fact]
    public void AcrylicIsTheMaterialWhereItCanBeDrawn() =>
        Assert.Equal(BackdropMaterial.Acrylic, BackdropChoice.For(requested: true, highContrast: false, acrylicSupported: true, micaSupported: true));

    [Fact]
    public void MicaStandsInWhereAcrylicCannotBeDrawn() =>
        Assert.Equal(BackdropMaterial.Mica, BackdropChoice.For(requested: true, highContrast: false, acrylicSupported: false, micaSupported: true));

    [Fact]
    public void AWindowWithNeitherMaterialStandsOnItsGround() =>
        Assert.Equal(BackdropMaterial.Solid, BackdropChoice.For(requested: true, highContrast: false, acrylicSupported: false, micaSupported: false));

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AReaderWhoTurnedTheBackdropOffGetsNoMaterial(bool acrylic, bool mica) =>
        Assert.Equal(BackdropMaterial.Solid, BackdropChoice.For(requested: false, highContrast: false, acrylic, mica));

    /// <summary>No preference puts translucency back over high contrast, Mica included.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void HighContrastTakesEveryMaterialAway(bool acrylic, bool mica) =>
        Assert.Equal(BackdropMaterial.Solid, BackdropChoice.For(requested: true, highContrast: true, acrylic, mica));
}
