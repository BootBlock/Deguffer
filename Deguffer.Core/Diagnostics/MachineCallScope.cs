namespace Deguffer.Core.Diagnostics;

/// <summary>
/// The span a <see cref="MachineCallTally"/> counts a flow for, from <see cref="MachineCallTally.CountIn"/>
/// to <see cref="Dispose"/>. A value, so opening one allocates nothing.
/// </summary>
internal readonly struct MachineCallScope : IDisposable
{
    private readonly MachineCallTally? _outer;

    internal MachineCallScope(MachineCallTally? outer)
    {
        _outer = outer;
    }

    /// <summary>Stop counting, and give the flow back to the tally that counted it before, if any.</summary>
    public void Dispose() => MachineCallTally.Restore(_outer);
}
