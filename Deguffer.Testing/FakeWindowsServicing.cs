using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// A machine a test can put in the middle of an update: a restart owed, files a restart will move, an
/// uninstall window of any length.
/// </summary>
public sealed class FakeWindowsServicing : IWindowsServicing
{
    /// <summary>A machine that has finished every update and keeps Windows' default ten days.</summary>
    public static FakeWindowsServicing Settled => new();

    /// <summary>
    /// Settable, so a test can owe a restart between the preview and the clean. Windows Update's key,
    /// which no call of Deguffer's can account for.
    /// </summary>
    public bool IsRestartPending { get; set; }

    /// <summary>
    /// The servicing stack's session key, last written at this instant, or null where it is absent: what
    /// the stack shows for the length of any DISM session. Settable, so a fake DISM can create it.
    /// </summary>
    public DateTimeOffset? ServicingSessionWritten { get; set; }

    /// <summary>What a restart will rename or delete, in display form.</summary>
    public IReadOnlyList<string> PendingFileOperations { get; set; } = [];

    public int UninstallWindowDays { get; init; } = 10;

    public RestartOwed ReadRestart() => new(
    [
        .. ServicingSessionWritten is { } written ? [new RestartMark(RestartKey.ServicingRebootPending, written)] : Array.Empty<RestartMark>(),
        .. IsRestartPending ? [new RestartMark(RestartKey.WindowsUpdateRebootRequired, DateTimeOffset.UtcNow)] : Array.Empty<RestartMark>(),
    ]);

    public bool HasPendingOperationsIn(string directory) =>
        LongPath.MayContainAny(directory, PendingFileOperations);
}
