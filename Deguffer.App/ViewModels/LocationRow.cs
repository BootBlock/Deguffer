using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Duplicates;

namespace Deguffer.App.ViewModels;

/// <summary>One location in the Duplicates page's list, and whether it is a reference.</summary>
public sealed partial class LocationRow : ObservableObject
{
    private readonly Action<LocationRow> _remove;

    /// <param name="remove">Takes the row out of the page's list.</param>
    public LocationRow(SearchLocation location, Action<LocationRow> remove)
    {
        Path = location.Path;
        IsReference = location.Role == LocationRole.Reference;
        _remove = remove;
    }

    /// <summary>The drive or folder, as the user picked it.</summary>
    public string Path { get; }

    /// <summary>
    /// Whether the location is a reference: searched and matched, and its copies never marked or
    /// removed (§7.4).
    /// </summary>
    [ObservableProperty]
    public partial bool IsReference { get; set; }

    public SearchLocation Location => new(Path, IsReference ? LocationRole.Reference : LocationRole.Search);

    /// <summary>The row's name for a screen reader, which says its role as well as its path.</summary>
    public string Description => IsReference ? $"{Path}, a reference" : Path;

    /// <summary>The role switch's name for a screen reader, which says which location it is for.</summary>
    public string SwitchName => $"{Path} is a reference";

    partial void OnIsReferenceChanged(bool value) => OnPropertyChanged(nameof(Description));

    [RelayCommand]
    private void Remove() => _remove(this);
}
