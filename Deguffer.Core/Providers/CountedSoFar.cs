using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The bytes one provider's measurements have counted so far while its plan is built, for the preview
/// to show while a large location is walked (§5.5).
///
/// <para>Each measurement reports a running figure of its own, from zero. A provider can make several,
/// one after another or at once, so each has a slot, and the sum of the slots is what is reported. A
/// slot holds its measurement's latest figure, which never falls, so the sum never falls either: a
/// second measurement starting does not send the figure back to nothing.</para>
/// </summary>
internal sealed class CountedSoFar(IProgress<ScanSize> counted)
{
    private readonly Lock _gate = new();
    private readonly List<ScanSize> _slots = [];
    private bool _ended;

    /// <summary>Where one measurement's running figure goes.</summary>
    public IProgress<ScanSize> Measurement()
    {
        lock (_gate)
        {
            _slots.Add(ScanSize.Zero);

            return new Slot(this, _slots.Count - 1);
        }
    }

    /// <summary>The plan is over, so nothing more is reported, whatever is still measuring.</summary>
    public void End()
    {
        lock (_gate)
        {
            _ended = true;
        }
    }

    private void Report(int slot, ScanSize size)
    {
        // One at a time, so the sum is read whole and the handler never runs beside itself, whichever
        // measurement's thread reports.
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            _slots[slot] = size;

            var sum = ScanSize.Zero;

            foreach (var each in _slots)
            {
                sum += each;
            }

            counted.Report(sum);
        }
    }

    private sealed class Slot(CountedSoFar owner, int index) : IProgress<ScanSize>
    {
        public void Report(ScanSize value) => owner.Report(index, value);
    }
}
