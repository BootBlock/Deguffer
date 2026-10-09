using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring;

namespace Deguffer.App.ViewModels;

/// <summary>
/// Where the Duplicates page searches (§7.4): the drives on offer, the locations chosen and the role
/// of each. Whether a drive or folder can join the list is <see cref="LocationChoice"/>'s to say.
/// </summary>
public sealed partial class DuplicateLocationsViewModel : ObservableObject
{
    private readonly DriveList _drives;

    /// <param name="given">The locations to start with: those an elevated relaunch was handed, or none.</param>
    public DuplicateLocationsViewModel(DriveList drives, IReadOnlyList<SearchLocation> given)
    {
        _drives = drives;

        foreach (var location in given)
        {
            Rows.Add(Row(location));
        }

        Rows.CollectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when a location joins or leaves the list, or changes its role.</summary>
    public event EventHandler? Changed;

    public ObservableCollection<DriveEntry> Drives => _drives.Entries;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddDriveCommand))]
    public partial DriveEntry? SelectedDrive { get; set; }

    public ObservableCollection<LocationRow> Rows { get; } = [];

    /// <summary>Why the last drive or folder picked was not added, or an empty string.</summary>
    [ObservableProperty]
    public partial string Note { get; private set; } = string.Empty;

    /// <summary>The locations as the search takes them, in the order they are listed.</summary>
    public IReadOnlyList<SearchLocation> Chosen => [.. Rows.Select(row => row.Location)];

    /// <summary>Add the folder the picker returned, or say why not.</summary>
    public void AddFolder(string picked) => Add(LocationChoice.WhyNotAdded(Chosen, picked), picked);

    /// <summary>Read the drives again for the picker, unless the last reading is fresh.</summary>
    public void RefreshDrives()
    {
        _drives.Refresh();
        SelectedDrive = _drives.Choose(SelectedDrive?.RootPath);
    }

    private bool CanAddDrive() => SelectedDrive is not null;

    [RelayCommand(CanExecute = nameof(CanAddDrive))]
    private void AddDrive()
    {
        if (SelectedDrive is { } drive)
        {
            Add(LocationChoice.WhyNotAdded(Chosen, drive.Choice), drive.RootPath);
        }
    }

    private void Add(string? whyNot, string path)
    {
        Note = whyNot ?? string.Empty;

        if (whyNot is null)
        {
            Rows.Add(Row(new SearchLocation(path)));
        }
    }

    private LocationRow Row(SearchLocation location)
    {
        var row = new LocationRow(location, removed => Rows.Remove(removed));

        // A role is part of the search, so changing it is asked about as adding a location is.
        row.PropertyChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);

        return row;
    }
}
