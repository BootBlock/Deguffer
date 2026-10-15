namespace Deguffer.Core.Scanning;

/// <summary>
/// One path of a measurement of several, and what its removal leaves standing. See
/// <see cref="IDirectoryScanner.MeasureManyAsync"/>.
/// </summary>
public readonly record struct ScanRequest(string Path, ScanBounds Bounds = default);
