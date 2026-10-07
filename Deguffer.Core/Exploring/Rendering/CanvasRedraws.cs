using System.Runtime.ExceptionServices;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The redraws of one canvas: each laid out and painted off the thread that asked for it, and only
/// the newest wanted.
///
/// <para>Off that thread because it is the UI thread. A redraw of a large tree at 4K is a layout of
/// tens of thousands of shapes and a pass over eight million pixels, and a scan asks for one every
/// three quarters of a second. Done where the pointer is heard, each was a pause in which nothing
/// the reader did had any effect.</para>
///
/// <para>The newest only, because anything newer is more current: a resize, a zoom arriving, or a
/// new snapshot of the tree. Starting one supersedes the one before, which puts nothing more on
/// screen from the moment it is superseded.</para>
///
/// <para>One at a time, too. A superseded redraw stops at the next region it would have painted, and
/// the next does not start until it has, so redraws never compete for the cores and one buffer
/// serves all of them in turn (G5).</para>
/// </summary>
public sealed class CanvasRedraws
{
    /// <summary>
    /// How many threads paint at once. One fewer than there are processors, so the thread the redraw
    /// was taken off of keeps a core to answer the pointer with, which is the point of taking it off.
    /// </summary>
    private static readonly int Workers = Math.Max(1, Environment.ProcessorCount - 1);

    private readonly SynchronizationContext _owner;

    private CanvasRedraw? _latest;

    /// <param name="owner">
    /// The thread every redraw is started and stopped on, and every region is put on screen on.
    /// </param>
    public CanvasRedraws(SynchronizationContext owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        _owner = owner;
    }

    /// <summary>The newest redraw, while it has not yet arrived and has not been stopped.</summary>
    public CanvasRedraw? Pending => _latest is { IsSettled: false } latest ? latest : null;

    /// <summary>
    /// Lay out a drawing with <paramref name="layout"/> and paint it on
    /// <paramref name="ground"/> into <paramref name="pixels"/>, handing it to
    /// <paramref name="target"/> as it goes. Supersedes any redraw still pending.
    /// </summary>
    /// <param name="layout">
    /// Called on a worker thread, so it must read nothing that belongs to the owner's thread: take
    /// what it needs as values before calling this.
    /// </param>
    /// <param name="pixels">
    /// A buffer of the drawing's size. It is not touched until every earlier redraw has stopped, so
    /// the one buffer can be handed to each redraw in turn.
    /// </param>
    /// <param name="focus">Where the pointer is on the new canvas, painted first, or null.</param>
    public CanvasRedraw Start(
        Func<ExploreSurface> layout,
        byte[] pixels,
        TileColour ground,
        ExplorePoint? focus,
        ICanvasRedrawTarget target)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(target);

        var previous = _latest;

        previous?.Supersede();

        var redraw = new CanvasRedraw(_owner, layout, pixels, ground, focus, target);

        redraw.Finished = (previous?.Finished ?? Task.CompletedTask).ContinueWith(
            _ => redraw.Run(Workers),
            CancellationToken.None,
            TaskContinuationOptions.DenyChildAttach,
            TaskScheduler.Default);

        // A layout or a paint that fails is a defect, and it is raised where it would have been
        // raised before the work moved off the owner's thread, rather than lost with the task.
        redraw.Finished.ContinueWith(
            failed => _owner.Post(
                state => ((ExceptionDispatchInfo)state!).Throw(),
                ExceptionDispatchInfo.Capture(failed.Exception!.Flatten().InnerException!)),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.DenyChildAttach,
            TaskScheduler.Default);

        _latest = redraw;

        return redraw;
    }

    /// <summary>
    /// Stop the redraw still pending, if there is one, for a canvas that no longer wants it. Says
    /// whether there was one, so the caller knows it is owed a redraw later.
    /// </summary>
    public bool Cancel()
    {
        var pending = Pending;

        pending?.Supersede();

        return pending is not null;
    }
}
