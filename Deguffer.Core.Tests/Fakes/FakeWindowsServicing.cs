using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// A machine a test can put in the middle of an update: a restart owed, files a restart will move, an
/// uninstall window of any length.
/// </summary>
public sealed class FakeWindowsServicing : IWindowsServicing
{
    /// <summary>A machine that has finished every update and keeps Windows' default ten days.</summary>
    public static FakeWindowsServicing Settled => new();

    public bool IsRestartPending { get; init; }

    /// <summary>What a restart will rename or delete, in display form.</summary>
    public IReadOnlyList<string> PendingFileOperations { get; init; } = [];

    public int UninstallWindowDays { get; init; } = 10;

    public bool HasPendingOperationsIn(string directory) =>
        PendingFileOperations.Any(path => LongPath.Contains(LongPath.Display(directory), path));
}
