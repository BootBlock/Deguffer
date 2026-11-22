using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Diagnostics;

/// <summary>
/// One volume's part of an <see cref="IndexBuildLog"/>: its build's start, end and changes of read
/// priority, and the questions about it each route answered. Every time is from the moment the log
/// was made.
///
/// <para>Its own lock, because the build reports from its own thread, a change of priority from the
/// thread that started or ended a walk, and an answer from the thread that asked.</para>
/// </summary>
internal sealed class IndexBuildTrace
{
    private readonly char _driveLetter;
    private readonly TimeProvider _time;
    private readonly long _opened;
    private readonly Lock _gate = new();

    private TimeSpan? _started;
    private TimeSpan? _ended;
    private bool _ready;
    private TimeSpan? _lowSince;
    private TimeSpan _low;
    private int _byTable;
    private int _byWalk;

    internal IndexBuildTrace(char driveLetter, TimeProvider time, long opened)
    {
        _driveLetter = driveLetter;
        _time = time;
        _opened = opened;
    }

    private TimeSpan Now => _time.GetElapsedTime(_opened);

    /// <summary>
    /// The volume's build opened its volume and is about to read the table, at normal priority. A
    /// build started again, after the one before it was dropped, starts the trace's build over.
    /// </summary>
    public void Started()
    {
        lock (_gate)
        {
            _started = Now;
            _ended = null;
            _ready = false;
            _lowSince = null;
            _low = TimeSpan.Zero;
        }
    }

    /// <summary>The build was asked to read at <paramref name="priority"/> from now on.</summary>
    public void Prioritised(ReadPriority priority)
    {
        lock (_gate)
        {
            if (_started is null || _ended is not null)
            {
                return;
            }

            if (priority is ReadPriority.Low)
            {
                _lowSince ??= Now;
            }
            else if (_lowSince is { } since)
            {
                _low += Now - since;
                _lowSince = null;
            }
        }
    }

    /// <summary>The build ended, with its index <paramref name="ready"/> to answer, or without one.</summary>
    public void Ended(bool ready)
    {
        lock (_gate)
        {
            if (_started is null || _ended is not null)
            {
                return;
            }

            var now = Now;
            _ended = now;
            _ready = ready;

            if (_lowSince is { } since)
            {
                _low += now - since;
                _lowSince = null;
            }
        }
    }

    /// <summary>One question about the volume, answered by the route <paramref name="answeredBy"/>.</summary>
    public void Answered(ScanStrategy answeredBy)
    {
        lock (_gate)
        {
            if (answeredBy is ScanStrategy.MasterFileTable)
            {
                _byTable++;
            }
            else
            {
                _byWalk++;
            }
        }
    }

    /// <summary>The trace as it stands, with a build still reading at low priority counted up to now.</summary>
    public IndexBuildTimes Times()
    {
        lock (_gate)
        {
            var low = _lowSince is { } since ? _low + (Now - since) : _low;

            return new IndexBuildTimes(_driveLetter, _started, _ready ? _ended : null, low, _byTable, _byWalk);
        }
    }
}
