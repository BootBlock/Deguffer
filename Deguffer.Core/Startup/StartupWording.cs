namespace Deguffer.Core.Startup;

/// <summary>
/// The one pair of words the Startup page uses for an entry's state (§7.5): the badge says what the
/// entry is, and the change button says what it will make it. Task Manager and Settings use the same
/// pair, so a reader who disables an entry here finds it under the same word there.
/// </summary>
public static class StartupWording
{
    /// <summary>The badge's word for <paramref name="state"/>.</summary>
    public static string For(StartupState state) => state switch
    {
        StartupState.Enabled => "Enabled",
        StartupState.Disabled => "Disabled",
        StartupState.Unknown => "Unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>The change button's word for <paramref name="change"/>.</summary>
    public static string For(StartupChange change) => change switch
    {
        StartupChange.Enable => "Enable",
        StartupChange.Disable => "Disable",
        _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
    };
}
