namespace Deguffer.Core.Diagnostics;

/// <summary>
/// The span a diagnostic recorder holds the caller's asynchronous flow for, from <see cref="Enter"/>
/// to <see cref="Dispose"/>, which gives the flow back to whatever recorder held it before. A value,
/// so opening one allocates nothing.
/// </summary>
/// <typeparam name="T">The recorder: <see cref="MachineCallTally"/> or <see cref="IndexBuildLog"/>.</typeparam>
internal readonly struct FlowScope<T> : IDisposable
    where T : class
{
    private readonly AsyncLocal<T?> _slot;
    private readonly T? _outer;

    private FlowScope(AsyncLocal<T?> slot, T? outer)
    {
        _slot = slot;
        _outer = outer;
    }

    /// <summary>Hand the caller's flow, and everything it starts, to <paramref name="recorder"/> through <paramref name="slot"/>.</summary>
    public static FlowScope<T> Enter(AsyncLocal<T?> slot, T recorder)
    {
        var outer = slot.Value;
        slot.Value = recorder;

        return new FlowScope<T>(slot, outer);
    }

    /// <summary>Stop recording, and give the flow back to the recorder that held it before, if any.</summary>
    public void Dispose() => _slot.Value = _outer;
}
