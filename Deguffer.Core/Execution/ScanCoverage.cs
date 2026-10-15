namespace Deguffer.Core.Execution;

/// <summary>
/// How much of what Deguffer recognises a Storage scan measured: <paramref name="Measured"/>
/// locations of <paramref name="Recognised"/>. Equal for a full scan, and fewer for a scan of the
/// locations the user chose. See <see cref="ScanScope"/>.
/// </summary>
public readonly record struct ScanCoverage(int Measured, int Recognised);
