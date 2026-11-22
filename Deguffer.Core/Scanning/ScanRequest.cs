namespace Deguffer.Core.Scanning;

/// <summary>
/// One path of a measurement of several, and what its removal leaves standing. See
/// <see cref="IDirectoryScanner.MeasureManyAsync"/>.
/// </summary>
public readonly record struct ScanRequest(string Path, ScanBounds Bounds = default)
{
    /// <summary>Refuse <paramref name="asked"/> where it is null or any of its paths is blank.</summary>
    /// <exception cref="ArgumentException">A path is null, empty or white space.</exception>
    public static void ThrowIfAnyPathIsBlank(IReadOnlyList<ScanRequest> asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        foreach (var question in asked)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(question.Path, nameof(asked));
        }
    }
}
