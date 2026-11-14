using Deguffer.Core.Configuration;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The appearance window's Names section: which shapes are named, in what lettering, at what size, how
/// many, and whether each name is followed by its size. Each box is indexed against the enum it sets,
/// in the enum's order.
/// </summary>
public sealed class MapNamesViewModel : MapAppearanceSection
{
    internal MapNamesViewModel(MapAppearanceViewModel window)
        : base(
            window,
            [
                nameof(FolderNames),
                nameof(ShapeNames),
                nameof(LetteringIndex),
                nameof(TextSizeIndex),
                nameof(NameCountIndex),
                nameof(Sizes),
                nameof(Named),
                nameof(OwnLettering),
                nameof(NamesCounted),
            ])
    {
    }

    /// <summary>Whether each treemap folder with room for one keeps a band with its name in it.</summary>
    public bool FolderNames
    {
        get => Look.Style.FolderNames;
        set => Window.Restyle(style => style with { FolderNames = value });
    }

    /// <summary>Whether a shape with room for it has its name and size written inside it.</summary>
    public bool ShapeNames
    {
        get => Look.Style.ShapeNames;
        set => Window.Restyle(style => style with { ShapeNames = value });
    }

    /// <summary>Index into the lettering list, ordered to match <see cref="MapLettering"/>.</summary>
    public int LetteringIndex
    {
        get => (int)Look.Style.Lettering;
        set => Window.Restyle(value, style => style with { Lettering = (MapLettering)value });
    }

    /// <summary>Index into the text size list, ordered to match <see cref="MapTextSize"/>.</summary>
    public int TextSizeIndex
    {
        get => (int)Look.Detail.TextSize;
        set => Window.Redetail(value, detail => detail with { TextSize = (MapTextSize)value });
    }

    /// <summary>Index into the list of how many shapes are named, ordered to match <see cref="MapNameCount"/>.</summary>
    public int NameCountIndex
    {
        get => (int)Look.Detail.Names;
        set => Window.Redetail(value, detail => detail with { Names = (MapNameCount)value });
    }

    /// <summary>Whether each name is followed by its size.</summary>
    public bool Sizes
    {
        get => Look.Detail.Sizes;
        set => Window.Redetail(detail => detail with { Sizes = value });
    }

    /// <summary>Whether any name is written, so the lettering and the sizes change anything (<see cref="MapStyle.Named"/>).</summary>
    public bool Named => Look.Style.Named;

    /// <summary>Whether the names are in the application's lettering, whose size can be chosen (<see cref="MapStyle.OwnLettering"/>).</summary>
    public bool OwnLettering => Look.Style.OwnLettering;

    /// <summary>Whether the number of names changes anything (<see cref="MapStyle.NamesCounted"/>).</summary>
    public bool NamesCounted => Look.Style.NamesCounted;
}
