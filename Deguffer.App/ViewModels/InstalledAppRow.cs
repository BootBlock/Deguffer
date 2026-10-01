using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.InstalledApps;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One entry on the Installed apps page. Written over in place when a new reading arrives, so the
/// list keeps its selection and the reader keeps their place (G4).
/// </summary>
public sealed partial class InstalledAppRow : ObservableObject
{
    public InstalledAppRow(InstalledEntry entry, EntryMarks marks) => Show(entry, marks);

    public UninstallKey Key => Entry.Key;

    [ObservableProperty]
    public partial InstalledEntry Entry { get; private set; }

    [ObservableProperty]
    public partial string Name { get; private set; }

    /// <summary>Publisher, version and who the entry is for, as one line.</summary>
    [ObservableProperty]
    public partial string Detail { get; private set; }

    /// <summary>The standing badge's word.</summary>
    [ObservableProperty]
    public partial string Standing { get; private set; }

    /// <summary>Which standing badge style the row wears.</summary>
    [ObservableProperty]
    public partial EntryStanding StandingKind { get; private set; }

    /// <summary>Why the entry is in its list: the standing badge's tooltip.</summary>
    [ObservableProperty]
    public partial string Reason { get; private set; }

    /// <summary>Why Windows hides the entry, or empty where it lists it.</summary>
    [ObservableProperty]
    public partial string Hidden { get; private set; }

    [ObservableProperty]
    public partial bool IsHidden { get; private set; }

    /// <summary>What needs administrator rights, or empty where nothing does.</summary>
    [ObservableProperty]
    public partial string Shield { get; private set; }

    [ObservableProperty]
    public partial bool HasShield { get; private set; }

    /// <summary>Why the program cannot be uninstalled, or empty where it can.</summary>
    [ObservableProperty]
    public partial string Refusal { get; private set; }

    [ObservableProperty]
    public partial bool HasRefusal { get; private set; }

    [ObservableProperty]
    public partial string Size { get; private set; }

    [ObservableProperty]
    public partial bool HasSize { get; private set; }

    /// <summary>Everything the row's marks say, for a screen reader.</summary>
    [ObservableProperty]
    public partial string Description { get; private set; }

    public void Show(InstalledEntry entry, EntryMarks marks)
    {
        Entry = entry;
        Name = entry.Name;
        Detail = string.Join(" · ", new[] { entry.Publisher, entry.Version, entry.Key.Scope.Describe() }.Where(p => p is not null));
        Standing = marks.Standing;
        StandingKind = entry.Standing.Standing;
        Reason = marks.Reason;
        Hidden = marks.Hidden ?? string.Empty;
        IsHidden = marks.Hidden is not null;
        Shield = marks.Shield ?? string.Empty;
        HasShield = marks.Shield is not null;
        Refusal = marks.Refusal ?? string.Empty;
        HasRefusal = marks.Refusal is not null;
        Size = marks.Size is { } bytes ? FreeSpace.Format(bytes) : string.Empty;
        HasSize = marks.Size is not null;
        Description = string.Join(" ", new[] { $"{marks.Standing}.", marks.Reason, marks.Hidden, marks.Shield, marks.Refusal, HasSize ? $"Its installer estimated {Size}." : null }.Where(p => p is not null));
    }
}
