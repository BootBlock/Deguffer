using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Health;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Storage page's line about the drive its free space is on (§7.6): what Health last found wrong
/// with the drives under the profile's volume, and nothing where it found nothing or has not read
/// them. The wording is Core's (<see cref="VolumeHealthNote"/>), and this follows it as it changes.
/// </summary>
public sealed partial class DriveHealthLine : ObservableObject
{
    private readonly HealthByVolume _health;
    private readonly IVolumeInventory _volumes;
    private readonly string _path;

    /// <param name="health">What Health last read of each volume's drives.</param>
    /// <param name="volumes">Where the volume holding <paramref name="path"/> is found.</param>
    /// <param name="path">A folder on the volume the page states the free space of.</param>
    public DriveHealthLine(HealthByVolume health, IVolumeInventory volumes, string path)
    {
        _health = health;
        _volumes = volumes;
        _path = path;

        // Lives as long as the page, which is kept for the whole session, as the record is.
        _health.Changed += (_, _) => Show();
        Show();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConcern), nameof(Title), nameof(Text), nameof(Severity))]
    public partial VolumeHealthNote? Note { get; private set; }

    public bool HasConcern => Note is not null;

    /// <summary>The verdict in words, so the line never depends on the bar's colour or icon (§6.5).</summary>
    public string Title => Note is { } note ? $"Drive health: {note.Word}" : string.Empty;

    public string Text => Note?.Text ?? string.Empty;

    public InfoBarSeverity Severity => Note?.Verdict is DriveVerdict.Failing ? InfoBarSeverity.Error : InfoBarSeverity.Warning;

    /// <summary>
    /// The volume is looked up only once there is a concern to state. Until Health has read, the
    /// volumes may not have been listed, and listing them here would ask every volume on the UI
    /// thread; once it has, Health's own reading has listed them.
    /// </summary>
    private void Show() =>
        Note = _health.HasConcern && HostVolume.For(_volumes, _path) is { } volume ? _health.Of(volume.RootPath) : null;
}
