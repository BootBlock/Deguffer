namespace Deguffer.Benchmark;

/// <summary>
/// What a route built and a scan would keep, held so that <see cref="Timing"/> can let it go.
///
/// <para>A holder rather than the result itself, because a copy of the tally carrying the result can
/// outlive the line that last uses it: a Debug build keeps every local alive to the end of its method.
/// Releasing the one holder lets the result go whatever copies of the tally remain, so the heap
/// measured without it really is without it.</para>
/// </summary>
internal sealed class KeptResult(object result)
{
    private object? _result = result;

    /// <summary>The result, or null once it is let go.</summary>
    public object? Result => _result;

    /// <summary>Let the result go.</summary>
    public void Release() => _result = null;
}
