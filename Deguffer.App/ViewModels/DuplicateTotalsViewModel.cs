using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The figures over every group a search has confirmed so far: how many groups, how many files they
/// hold, and what their copies that could go occupy. Counted as each group arrives, from what the
/// group already measured, so a search confirming forty thousand groups never sums the list again,
/// and shown once for each batch of groups the page takes rather than once for each group.
/// </summary>
public sealed partial class DuplicateTotalsViewModel : ObservableObject
{
    private int _groups;
    private int _files;

    /// <summary>What every group's copies that could go occupy, which a removal may free less than.</summary>
    internal long FreeableSpace { get; private set; }

    public string Freeable => FreeSpace.Format(FreeableSpace);

    public string Groups => _groups.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    public string GroupsLabel => _groups == 1 ? "group" : "groups";

    public string Files => _files.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Everything the figures say, in one sentence, for a screen reader.</summary>
    public string Description => $"{Groups} {GroupsLabel}, {Files} files in them. Copies that could go occupy {Freeable}.";

    /// <summary>A search began: the groups the figures counted are gone.</summary>
    internal void Clear()
    {
        (_groups, _files, FreeableSpace) = (0, 0, 0);
        Changed();
    }

    /// <summary>Count a group the search confirmed, shown with the rest of its batch (<see cref="Show"/>).</summary>
    internal void Add(DuplicateGroupRow group)
    {
        _groups++;
        _files += group.Count;
        FreeableSpace += group.FreeableSpace;
    }

    /// <summary>Show the groups counted since the figures were last shown.</summary>
    internal void Show() => Changed();

    private void Changed()
    {
        OnPropertyChanged(nameof(Freeable));
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(GroupsLabel));
        OnPropertyChanged(nameof(Files));
        OnPropertyChanged(nameof(Description));
    }
}
