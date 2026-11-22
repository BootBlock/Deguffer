namespace Deguffer.Core.Execution;

/// <summary>
/// When a clean last set anything going that could change the machine, so a reading a tool gave since
/// can stand for the next one the clean asks before anything acts again.
///
/// <para><b>Why a clean needs it.</b> DISM's analysis of the component store takes a minute or more,
/// and the run asks it immediately before each command over the store and again after. Where the
/// cleanup and the reset run in one clean, the cleanup's analysis after its command and the reset's
/// analysis before its own read the same store with nothing run between them. This record is what says
/// that nothing ran, so the second can be the first (see <see cref="Providers.ComponentStoreAnalysis"/>).</para>
///
/// <para><b>Acting is whatever could write.</b> A command step acts from its command to the command's
/// end: what comes before it, its checks, its look and its tool's measurement, only reads. Every other
/// step acts for the whole of its length, because a removal, a Disk Cleanup handler, and a release that
/// closes a program before a deletion each write from where they start. Each start and each end moves
/// the record on, so a reading begun while nothing acted holds only until the next one starts.</para>
///
/// <para><b>One for the whole clean</b>, for the reason <see cref="RunResidue"/> is one: what could
/// change a store one plan measured can belong to any other plan in the clean. A reading never crosses
/// from one clean to the next, because the time between two cleans is time Windows can service its
/// store in.</para>
/// </summary>
public sealed class RunActivity
{
    private readonly Lock _gate = new();
    private long _moves;
    private int _acting;

    /// <summary>
    /// Records that something which could change the machine starts now, and ends when the returned
    /// scope is disposed.
    /// </summary>
    public Acting Begin()
    {
        lock (_gate)
        {
            _acting++;
            _moves++;
        }

        return new Acting(this);
    }

    /// <summary>
    /// A mark for a reading that begins now, or null where something is acting, because a reading taken
    /// while a step writes may be of the machine before the write, after it, or part-way through it.
    /// </summary>
    public QuietMark? Quiet()
    {
        lock (_gate)
        {
            return _acting == 0 ? new QuietMark(this, _moves) : null;
        }
    }

    /// <summary>
    /// Whether nothing in this clean has started acting since <paramref name="mark"/> was taken. A mark
    /// taken in another clean never holds here.
    /// </summary>
    public bool StillQuiet(QuietMark mark)
    {
        lock (_gate)
        {
            return ReferenceEquals(mark.Run, this) && mark.Moves == _moves;
        }
    }

    private void End()
    {
        lock (_gate)
        {
            _acting--;
            _moves++;
        }
    }

    /// <summary>Something acting, from <see cref="Begin"/> until it is disposed.</summary>
    public readonly struct Acting : IDisposable
    {
        private readonly RunActivity? _run;

        internal Acting(RunActivity run) => _run = run;

        public void Dispose() => _run?.End();
    }
}

/// <summary>Where a clean's activity stood when a reading began. See <see cref="RunActivity.Quiet"/>.</summary>
/// <param name="Run">The clean the reading was taken in.</param>
/// <param name="Moves">How often anything had started or ended acting in that clean by then.</param>
public readonly record struct QuietMark(RunActivity Run, long Moves);
