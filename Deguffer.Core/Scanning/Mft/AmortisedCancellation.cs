namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// A cancellation check for a pass over a volume's table, paid once every 65,536 steps rather than
/// on each.
///
/// <para>A pass from a folder near the volume root steps through millions of nodes, so a check on
/// every one would cost more than the step it guards, and a pass with none runs to its end after the
/// caller has gone. One step is a few array reads, so the stride answers a cancellation within a
/// millisecond or two.</para>
/// </summary>
internal struct AmortisedCancellation(CancellationToken ct)
{
    private const int StrideMask = 0xFFFF;

    private int _steps;

    /// <summary>Count one step, and throw on the stride where the caller has cancelled.</summary>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public void Step()
    {
        if ((++_steps & StrideMask) == 0)
        {
            ct.ThrowIfCancellationRequested();
        }
    }
}
