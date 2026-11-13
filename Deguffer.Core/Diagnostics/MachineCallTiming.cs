namespace Deguffer.Core.Diagnostics;

/// <summary>
/// One <see cref="MachineCall"/> under way, counted in its tally when disposed. A value, because it
/// wraps calls made for every path a pass follows (G4), and the default one, made where no tally
/// counts, does nothing.
/// </summary>
internal readonly struct MachineCallTiming(MachineCallTally? tally, MachineCall call, long started) : IDisposable
{
    public void Dispose() => tally?.Ended(call, started);
}
