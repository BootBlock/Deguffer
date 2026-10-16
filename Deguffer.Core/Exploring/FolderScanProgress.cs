namespace Deguffer.Core.Exploring;

/// <summary>
/// The progress of a scan of several folders, made from the reports of its passes, each a table read
/// or a walk, some of them running at once.
///
/// <para><b>One running count, never one pass's.</b> Each pass counts from zero, so handing on
/// whichever pass reported last would show two counts in turn while two run, and a count that falls
/// as each pass starts. Each report is every pass's latest added together: what finished passes saw,
/// and what running ones have seen so far.</para>
///
/// <para><b>A total only while every pass that has reported has one.</b> A table read knows its record
/// count, and a walk cannot know what it has yet to open. A sum with a walk in it has no total, and
/// says so rather than giving a fraction of only some of the work.</para>
///
/// <para>Reported under the lock that adds it up, so the reports go on in the order their sums were
/// made, and a later sum is never overtaken by an earlier one.</para>
/// </summary>
internal sealed class FolderScanProgress(IProgress<ExploreProgress> progress)
{
    private readonly Lock _gate = new();
    private readonly List<ExploreProgress> _latest = [];

    /// <summary>The progress one pass reports to, which adds its reports to every other pass's.</summary>
    public IProgress<ExploreProgress> Pass()
    {
        int slot;

        lock (_gate)
        {
            slot = _latest.Count;
            _latest.Add(new ExploreProgress(0, Total: 0, BytesSeen: 0));
        }

        return new Slot(this, slot);
    }

    private void Report(int slot, ExploreProgress value)
    {
        lock (_gate)
        {
            _latest[slot] = value;

            long done = 0, bytes = 0;
            long? total = 0;

            foreach (var pass in _latest)
            {
                done += pass.Done;
                bytes += pass.BytesSeen;
                total = total is { } sum && pass.Total is { } passTotal ? sum + passTotal : null;
            }

            progress.Report(new ExploreProgress(done, total, bytes));
        }
    }

    private sealed class Slot(FolderScanProgress sum, int slot) : IProgress<ExploreProgress>
    {
        public void Report(ExploreProgress value) => sum.Report(slot, value);
    }
}
