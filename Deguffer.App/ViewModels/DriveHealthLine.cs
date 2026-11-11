using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Health;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Storage page's line about the drive its free space is on (§7.6): what Health last found wrong
/// with the drives under that volume, and nothing where it found nothing or has not read them. The
/// wording is Core's (<see cref="VolumeHealthNote"/>), and this follows it as it changes.
/// </summary>
public sealed partial class DriveHealthLine : ObservableObject
{
    /// <param name="health">What Health last read of each volume's drives.</param>
    /// <param name="mountPoint">
    /// Where the volume is mounted, or null where Windows would not say, which leaves the line closed.
    /// Given rather than looked up here, because a look-up on each change of Health's reading could
    /// list every volume on the UI thread while Health's own listing was still under way.
    /// </param>
    public DriveHealthLine(HealthByVolume health, string? mountPoint)
    {
        if (mountPoint is null)
        {
            return;
        }

        // Lives as long as the page, which is kept for the whole session, as the record is.
        health.Changed += (_, _) => Note = health.Of(mountPoint);
        Note = health.Of(mountPoint);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConcern), nameof(Title), nameof(Text), nameof(Severity))]
    public partial VolumeHealthNote? Note { get; private set; }

    public bool HasConcern => Note is not null;

    /// <summary>The verdict in words, so the line never depends on the bar's colour or icon (§6.5).</summary>
    public string Title => Note is { } note ? $"Drive health: {note.Word}" : string.Empty;

    public string Text => Note?.Text ?? string.Empty;

    public InfoBarSeverity Severity => Note?.Verdict is DriveVerdict.Failing ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
}
