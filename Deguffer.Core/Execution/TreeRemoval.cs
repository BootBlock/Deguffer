using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// Deletes a directory tree as it is listed: each folder's files and links as soon as its own listing
/// has been read to the end, and each folder as soon as everything in it has been tried.
///
/// <para><b>Beside the walk, not after it.</b> A removal that listed the whole tree before deleting
/// anything kept the disk idle on deletions for as long as the walk took, and idle on listings for as
/// long as the deletions took. Here they overlap, and only what a folder's own complete listing
/// produced is ever handed to a delete: a folder whose listing failed contributes nothing, whether or
/// not the rest of the tree has been listed. A removal that must go whole or not at all is judged on
/// a walk run to its end first, and then deleted here from what that walk gathered. See
/// <see cref="WholeDirectoryRemoval"/>.</para>
///
/// <para><b>One bound for the whole removal.</b> Files, links and folders are taken by one crew of at
/// most <see cref="DirectoryRemover.MostAtOnce"/> threads, the thread that runs the walk joining it
/// once the walk is over. No folder is tried while anything inside it is still to be tried: see
/// <see cref="RemovalFolder"/>.</para>
///
/// <para><b>And one bound for every removal that runs beside it.</b> Each thread that deletes holds a
/// place in a <see cref="ThreadAllowance"/> while it does, the walk's thread only once the walk is over,
/// so removals that run at once share the bound rather than each taking it whole.</para>
/// </summary>
internal sealed class TreeRemoval
{
    private readonly ConcurrentQueue<Work> _queue = new();
    private readonly WorkerCrew _crew;
    private readonly ThreadAllowance _deletions;
    private readonly RemovalBounds _bounds;
    private readonly RemovalProgress _progress;
    private readonly IFileSystem _fs;
    private readonly CancellationToken _ct;

    // Cancelled where the removal is, and also where a deletion failed, so the walk stops either way.
    private readonly CancellationTokenSource _stop;
    private readonly Func<bool> _readyToLook;
    private readonly Action _offered;

    private readonly RemovalTally _tally;
    private readonly TimeProvider _time;
    private readonly PassSpan _files;
    private readonly PassSpan _folders;

    // Work queued and not yet done. The removal is over once the walk is and this is zero.
    private int _pending;

    // Every entry the walk gathered: each folder it listed, and the files and links in it.
    private long _walked;

    // Set where the removal stopped with work left: a listing not handed over, a folder not climbed
    // to, or work still queued.
    private bool _abandoned;

    private TreeRemoval(
        string root,
        RemovalBounds bounds,
        RemovalProgress progress,
        IFileSystem fs,
        TimeProvider time,
        ThreadAllowance deletions,
        CancellationToken ct)
    {
        _time = time;
        _deletions = deletions;
        _files = new PassSpan(time);
        _folders = new PassSpan(time);
        _tally = new RemovalTally(root);
        _bounds = bounds;
        _progress = progress;
        _fs = fs;
        _ct = ct;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Action drain = Drain;
        _crew = new WorkerCrew(DirectoryRemover.MostAtOnce, () => drain, () => !Stopping && !_queue.IsEmpty, deletions);
        _offered = _crew.OfferPlace;
        _readyToLook = () => !_queue.IsEmpty || Stopping || Volatile.Read(ref _pending) == 0;
    }

    private bool Stopping => _crew.Failed || _ct.IsCancellationRequested;

    /// <summary>
    /// Remove the tree at <paramref name="root"/> as <paramref name="walk"/> lists it, and say what
    /// became of it. Cancellation is never thrown: a removal stopped part-way returns what it took,
    /// with <see cref="RemovalOutcome.Interrupted"/> set.
    /// </summary>
    /// <param name="walk">
    /// Lists the tree, handing each folder to its first argument once the folder's own listing is
    /// complete, and stopping with <see cref="OperationCanceledException"/> when its second is
    /// cancelled. A tree already gathered is handed over folder by folder.
    /// </param>
    /// <param name="planned">How many entries the plan measured, for progress until the walk is over.</param>
    /// <param name="time">The clock <see cref="RemovalOutcome.Passes"/> is measured on.</param>
    /// <param name="gathered">
    /// The walk that gathered a tree handed over from what it gathered, which is the removal's walk
    /// pass. Null where <paramref name="walk"/> lists the tree, which is then timed as the walk.
    /// </param>
    /// <param name="deletions">Where each thread that deletes takes its place, shared with every removal beside this one.</param>
    public static RemovalOutcome Run(
        RemovalFolder root,
        RemovalBounds bounds,
        Action<Action<RemovalFolder>, CancellationToken> walk,
        IProgress<double>? progress,
        long planned,
        IFileSystem fs,
        TimeProvider time,
        PassTiming? gathered,
        ThreadAllowance deletions,
        CancellationToken ct)
    {
        var removal = new TreeRemoval(root.FoundAs, bounds, new RemovalProgress(progress, planned), fs, time, deletions, ct);

        try
        {
            return removal.Run(root, walk, gathered);
        }
        finally
        {
            removal._stop.Dispose();
        }
    }

    private RemovalOutcome Run(RemovalFolder root, Action<Action<RemovalFolder>, CancellationToken> walk, PassTiming? gathered)
    {
        using var wake = _crew.WakeOn(_ct);

        // A place another removal gives back starts a helper here where this removal was refused one for
        // work it still has queued. Without it, that work would be left to the threads it already has.
        using var offered = _deletions.WhenReturned(_offered);

        var walked = false;
        var walkStarted = _time.GetTimestamp();

        try
        {
            walk(Listed, _stop.Token);
            walked = true;
            _progress.Counted();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Stopped by the clean's cancellation, or by a deletion that failed, which Finish throws.
        }
        catch (Exception ex)
        {
            // Held, not thrown here: the crew may still be inside a deletion. Finish throws it,
            // unchanged, once every helper has ended.
            _crew.Fail(ex);
        }

        var walkTook = gathered ?? new PassTiming(_time.GetElapsedTime(walkStarted), Interlocked.Read(ref _walked));

        // The walk is over, so this thread joins the crew until the last of the work is done, once it
        // holds a place of its own. False where the removal is cancelled while it waits for one.
        if (walked && !Stopping && _deletions.Take(_ct))
        {
            try
            {
                DrainToTheEnd();
            }
            finally
            {
                _deletions.Return();
            }
        }

        _crew.Finish();

        var finished = walked && Volatile.Read(ref _pending) == 0 && !Volatile.Read(ref _abandoned);
        if (finished)
        {
            _progress.Finished();
        }

        return _tally.Outcome(root, _bounds, _fs, interrupted: !finished) with
        {
            Passes = new RemovalPasses(walkTook, _files.Timing, _folders.Timing),
        };
    }

    /// <summary>Take queued work on the thread that ran the walk until none is left or the removal is stopping.</summary>
    private void DrainToTheEnd()
    {
        while (!Stopping)
        {
            Drain();

            if (Volatile.Read(ref _pending) == 0)
            {
                return;
            }

            _crew.WaitUnless(_readyToLook);
        }
    }

    /// <summary>
    /// Hand <paramref name="folder"/>'s files and links to the crew, and the folder itself where
    /// nothing in it is left to wait for. On the walk's threads, as each listing completes.
    /// </summary>
    private void Listed(RemovalFolder folder)
    {
        var entries = folder.Files.Count + folder.Links.Count;

        Interlocked.Add(ref _walked, 1 + entries);

        if (Stopping)
        {
            // Cancelled, or a deletion failed: stop the walk too, so a failure is thrown without listing
            // the rest of the tree.
            Volatile.Write(ref _abandoned, true);
            _stop.Cancel();
            return;
        }

        var itself = folder.Parent is null && _bounds.KeepRoot ? 0 : 1;

        _progress.Found(entries + itself);

        // Counted before anything is queued, so no deletion can finish the folder before its listing
        // has been counted done below.
        folder.Expect(entries);
        Interlocked.Add(ref _pending, entries);

        for (var entry = 0; entry < entries; entry++)
        {
            _queue.Enqueue(new Work(folder, entry));
        }

        var queued = entries;

        if (folder.Finish())
        {
            Interlocked.Increment(ref _pending);
            _queue.Enqueue(new Work(folder, Work.Itself));
            queued++;
        }

        StartHelpers(queued);
    }

    /// <summary>
    /// Start a helper for each of <paramref name="wanted"/> pieces of work just queued, as far as the
    /// bound allows. The thread that runs the walk is busy listing, so every deletion until the walk is
    /// over is a helper's.
    /// </summary>
    private void StartHelpers(int wanted)
    {
        for (var started = 0; started < wanted; started++)
        {
            if (!_crew.TryStartHelper())
            {
                return;
            }
        }
    }

    /// <summary>Take queued work until none is left or the removal is stopping.</summary>
    private void Drain()
    {
        while (!Stopping && _queue.TryDequeue(out var work))
        {
            if (work.Entry == Work.Itself)
            {
                Climb(work.Folder);
            }
            else
            {
                Delete(work.Folder, work.Entry);
            }

            if (Interlocked.Decrement(ref _pending) == 0)
            {
                _crew.Signal();
            }
        }
    }

    /// <summary>
    /// Delete the entry of <paramref name="folder"/> at <paramref name="entry"/>: one of its files, or
    /// after them one of its links. A link is removed as a link and holds no bytes of its own, so a
    /// refusal to remove one moves no figure. A directory link that stays is still recorded as
    /// standing, because what §5.6 reads from it is where the removal went, not what it weighed.
    /// </summary>
    private void Delete(RemovalFolder folder, int entry)
    {
        _tally.Attempting();
        var began = _files.Beginning();

        var files = folder.Files;
        if (entry < files.Count)
        {
            var file = files[entry];

            if (FileDeletion.TryDelete(file.FullName, file.Attributes, _fs) is { } reason)
            {
                _tally.Refused(file, reason);
            }
            else
            {
                _tally.Removed(file.Length);
            }
        }
        else
        {
            var link = folder.Links[entry - files.Count];
            var gone = link.IsDirectory
                ? DirectoryDeletion.TryDelete(link.FullName, _fs) is null
                : FileDeletion.TryDelete(link.FullName, link.Attributes, _fs) is null;

            if (gone)
            {
                _tally.Removed(bytes: 0);
            }
            else if (link.IsDirectory)
            {
                _tally.Stayed(link.FullName);
            }
        }

        _files.Ended(began);
        _progress.Done();

        if (folder.Finish())
        {
            Climb(folder);
        }
    }

    /// <summary>
    /// Try <paramref name="folder"/>, now that nothing in it is left to try, and each folder above it
    /// that this leaves with nothing to wait for. Trying a parent whose child stayed is still right:
    /// Windows refuses it as not empty, which is how a folder held up by what is inside it is recorded.
    /// </summary>
    private void Climb(RemovalFolder folder)
    {
        for (var next = folder; ; next = next.Parent)
        {
            // A removal stopping leaves the folders above where it stopped untried, which the outcome
            // names. The work already under way finishes, as a deletion's does.
            if (Stopping)
            {
                Volatile.Write(ref _abandoned, true);
                return;
            }

            // The root is the last folder any climb reaches, so skipping it here cannot leave a
            // folder behind.
            if (next.Parent is not null || !_bounds.KeepRoot)
            {
                _tally.Attempting();
                var began = _folders.Beginning();
                next.Record(DirectoryDeletion.TryDelete(next.Path, _fs));
                _folders.Ended(began);
                _progress.Done();
            }

            next.ReleaseEntries();

            if (next.Parent is null || !next.Parent.Finish())
            {
                return;
            }
        }
    }

    /// <summary>
    /// One piece of the crew's work: an entry of a folder, its files first and then its links, or the
    /// folder itself.
    /// </summary>
    private readonly record struct Work(RemovalFolder Folder, int Entry)
    {
        public const int Itself = -1;
    }
}
