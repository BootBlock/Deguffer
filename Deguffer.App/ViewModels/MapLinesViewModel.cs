using Deguffer.Core.Configuration;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The appearance window's Lines section: how prominent the line round each file and each folder is,
/// how wide and in what tone, and whether shapes side by side share one. Each box is indexed against
/// the enum it sets, in the enum's order.
/// </summary>
public sealed class MapLinesViewModel : MapAppearanceSection
{
    internal MapLinesViewModel(MapAppearanceViewModel window)
        : base(
            window,
            [
                nameof(BlockBordersIndex),
                nameof(FolderBordersIndex),
                nameof(BorderWidthIndex),
                nameof(BorderToneIndex),
                nameof(LinesIndex),
                nameof(Outlined),
            ])
    {
    }

    /// <summary>Index into the list of lines round each block, ordered to match <see cref="MapBorder"/>.</summary>
    public int BlockBordersIndex
    {
        get => (int)Look.Style.BlockBorders;
        set => Window.Restyle(value, style => style with { BlockBorders = (MapBorder)value });
    }

    /// <summary>Index into the list of lines round each folder, ordered to match <see cref="MapBorder"/>.</summary>
    public int FolderBordersIndex
    {
        get => (int)Look.Style.FolderBorders;
        set => Window.Restyle(value, style => style with { FolderBorders = (MapBorder)value });
    }

    /// <summary>Index into the line width list, ordered to match <see cref="MapBorderWidth"/>.</summary>
    public int BorderWidthIndex
    {
        get => (int)Look.Style.BorderWidth;
        set => Window.Restyle(value, style => style with { BorderWidth = (MapBorderWidth)value });
    }

    /// <summary>Index into the line tone list, ordered to match <see cref="MapBorderTone"/>.</summary>
    public int BorderToneIndex
    {
        get => (int)Look.Style.BorderTone;
        set => Window.Restyle(value, style => style with { BorderTone = (MapBorderTone)value });
    }

    /// <summary>Index into the list of ways shapes side by side draw their lines, ordered to match <see cref="MapLines"/>.</summary>
    public int LinesIndex
    {
        get => (int)Look.Style.Lines;
        set => Window.Restyle(value, style => style with { Lines = (MapLines)value });
    }

    /// <summary>Whether any line is drawn, so the width, the tone and the sharing change anything (<see cref="MapStyle.Outlined"/>).</summary>
    public bool Outlined => Look.Style.Outlined;
}
