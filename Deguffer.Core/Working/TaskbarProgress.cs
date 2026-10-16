namespace Deguffer.Core.Working;

/// <summary>What the taskbar button's progress bar shows.</summary>
public enum TaskbarState
{
    /// <summary>No bar: nothing is under way.</summary>
    None,

    /// <summary>A bar that moves without a value, for work that cannot say how far it has got.</summary>
    Indeterminate,

    /// <summary>A bar filled to the value.</summary>
    Normal,

    /// <summary>A red bar, for work that failed while the window was in the background.</summary>
    Error,
}

/// <summary>
/// The taskbar button's progress bar, with its fill in thousandths.
///
/// <para>Thousandths rather than the fraction itself, so two reports that would draw the same bar
/// compare equal and the button is not written again for them. A clean reports every 256 files, and
/// a write to the taskbar is a call into another process.</para>
/// </summary>
public sealed record TaskbarProgress(TaskbarState State, int Thousandths)
{
    public const int Whole = 1000;

    public static TaskbarProgress None { get; } = new(TaskbarState.None, 0);

    public static TaskbarProgress Indeterminate { get; } = new(TaskbarState.Indeterminate, 0);

    public static TaskbarProgress Error { get; } = new(TaskbarState.Error, Whole);

    /// <summary>A bar filled to <paramref name="fraction"/>, from 0 to 1.</summary>
    public static TaskbarProgress Filled(double fraction) =>
        new(TaskbarState.Normal, (int)Math.Round(Math.Clamp(fraction, 0, 1) * Whole));
}
