using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.InstalledApps;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One entry on the Installed apps page. Written over in place when a new reading arrives, so the
/// list keeps its selection and the reader keeps their place (G4).
/// </summary>
public sealed partial class InstalledAppRow : ObservableObject
{
    public InstalledAppRow(InstalledEntry entry) => Show(entry);

    public UninstallKey Key => Entry.Key;

    [ObservableProperty]
    public partial InstalledEntry Entry { get; private set; }

    [ObservableProperty]
    public partial string Name { get; private set; }

    /// <summary>Publisher, version and who the entry is for, as one line.</summary>
    [ObservableProperty]
    public partial string Detail { get; private set; }

    /// <summary>What the evidence showed, and why Windows hides the entry where it does.</summary>
    [ObservableProperty]
    public partial string Reason { get; private set; }

    public void Show(InstalledEntry entry)
    {
        Entry = entry;
        Name = entry.Name;
        Detail = string.Join(" · ", new[] { entry.Publisher, entry.Version, entry.Key.Scope.Describe() }.Where(p => p is not null));
        Reason = EntryListing.WhyHidden(entry.Visibility) is { } hidden
            ? $"{entry.Standing.Reason} {hidden}"
            : entry.Standing.Reason;
    }
}
