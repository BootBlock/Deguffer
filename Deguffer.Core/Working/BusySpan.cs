namespace Deguffer.Core.Working;

/// <summary>
/// One piece of work in <see cref="BusyWork"/>, from where it begins to where it is disposed.
///
/// <para>Once ended it ignores everything. A <see cref="Progress{T}"/> report is posted, so the last
/// ones from a worker can be delivered after the run has finished and its span gone, and one that
/// moved or failed an ended span would put the taskbar back to busy over an idle window.</para>
/// </summary>
public sealed class BusySpan : IDisposable
{
    private readonly BusyWork _owner;
    private bool _ended;

    internal BusySpan(BusyWork owner) => _owner = owner;

    /// <summary>How far the work has got, from 0 to 1, or null where it cannot say.</summary>
    public double? Fraction { get; private set; }

    /// <summary>
    /// Say how far the work has got, from 0 to 1, or null where it cannot say. A value outside that
    /// range is held to it. One that is not a number is taken as unknown: a scan that has counted
    /// nothing yet divides by zero.
    /// </summary>
    public void Report(double? fraction)
    {
        if (_ended)
        {
            return;
        }

        var held = fraction is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : (double?)null;

        if (held == Fraction)
        {
            return;
        }

        Fraction = held;
        _owner.Moved();
    }

    /// <summary>
    /// Say that the work failed: it stopped on an error, or a removal could not prove what it had to
    /// (§5.6). A cancellation is not a failure, because the user asked for it.
    /// </summary>
    public void Fail()
    {
        if (!_ended)
        {
            _owner.Fail();
        }
    }

    public void Dispose()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        _owner.End(this);
    }
}
