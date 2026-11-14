using System.Globalization;

namespace Deguffer.Core.Memory;

/// <summary>
/// When the figures on a memory page were read, said only while live update is off.
///
/// <para>§7.2: a snapshot is stale as soon as it is read. On the cadence the figures are never more
/// than a couple of seconds old, and saying so would be noise. Off it, they are as old as the reader's
/// last press, or older on a page they came back to, so the page says when they were read rather than
/// leave a picture of an hour ago standing as the present.</para>
/// </summary>
public static class MemoryReadTime
{
    /// <summary>
    /// What to say beside the live update switch: when <paramref name="read"/> was, in the reader's
    /// own clock and time format, or nothing while <paramref name="live"/> or before the first read.
    /// </summary>
    public static string Said(DateTimeOffset? read, bool live, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        return !live && read is { } at
            ? $"Read at {at.ToLocalTime().ToString("T", culture)}"
            : string.Empty;
    }
}
