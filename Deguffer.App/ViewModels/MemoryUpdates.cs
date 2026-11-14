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
    public string ReadAt => MemoryReadTime.Said(feed.LastRead, feed.Live, CultureInfo.CurrentCulture);

    public bool HasReadAt => ReadAt.Length > 0;

    /// <summary>A reading has been shown, so the time it was read is the one to say.</summary>
    public void Shown() => Restate();

    /// <summary>
    /// Read where memory is now, live or not. A read already running finishes first, so what arrives
    /// was read after the press.
    /// </summary>
    [RelayCommand]
    private void Refresh() => feed.Refresh();

    private void Restate()
    {
        OnPropertyChanged(nameof(ReadAt));
        OnPropertyChanged(nameof(HasReadAt));
    }
}
