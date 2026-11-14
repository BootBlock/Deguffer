using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Memory;

namespace Deguffer.App.ViewModels;

/// <summary>
/// When the Memory page reads the machine: on <see cref="MemoryFeed"/>'s cadence, or only when the
/// reader presses Refresh, and when the figures on screen were read while it is the second.
///
/// <para>The choice is kept by the feed rather than copied here, so the box and the readings cannot
/// disagree. What the page reads, and what it does with a reading, are
/// <see cref="MemoryViewModel"/>'s.</para>
/// </summary>
public sealed partial class MemoryUpdates(MemoryFeed feed) : ObservableObject
{
    /// <summary>When the reading on screen was read, or null before the first is shown.</summary>
    private DateTimeOffset? _shown;

    /// <summary>Whether the page reads on the cadence, or only when the reader presses Refresh.</summary>
    public bool Live
    {
        get => feed.Live;
        set
        {
            if (feed.Live == value)
            {
                return;
            }

            feed.Live = value;

            OnPropertyChanged();
            Restate();
        }
    }

    /// <summary>When the figures on screen were read, while live update is off. See <see cref="MemoryReadTime"/>.</summary>
    public string ReadAt { get; private set; } = string.Empty;

    public bool HasReadAt => ReadAt.Length > 0;

    /// <summary>The reading read at <paramref name="read"/> is the one on screen now.</summary>
    public void Shown(DateTimeOffset read)
    {
        _shown = read;
        Restate();
    }

    /// <summary>
    /// Read where memory is now, live or not. A read already running finishes first, so what arrives
    /// was read after the press.
    /// </summary>
    [RelayCommand]
    private void Refresh() => feed.Refresh();

    /// <summary>
    /// Say the time again, and tell the page only where the words changed: while live, every reading
    /// would otherwise tell it twice that nothing is said.
    /// </summary>
    private void Restate()
    {
        var said = MemoryReadTime.Said(_shown, feed.Live, TimeZoneInfo.Local, CultureInfo.CurrentCulture);

        if (said == ReadAt)
        {
            return;
        }

        var had = HasReadAt;

        ReadAt = said;
        OnPropertyChanged(nameof(ReadAt));

        if (had != HasReadAt)
        {
            OnPropertyChanged(nameof(HasReadAt));
        }
    }
}
