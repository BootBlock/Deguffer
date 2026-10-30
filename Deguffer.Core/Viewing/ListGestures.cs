namespace Deguffer.Core.Viewing;

/// <summary>
/// Where a list of rows a page rewrites stands between the rows it was given and the reader's hand on
/// it. See <see cref="ListGestures"/>.
/// </summary>
public enum ListGesture
{
    /// <summary>The list has settled on rows or containers it was given, and nobody has touched it since.</summary>
    Settled,

    /// <summary>
    /// The reader has pressed a key or a pointer in the list since it settled, and no press is held on
    /// a row.
    /// </summary>
    Touched,

    /// <summary>
    /// A pointer is down on a row right now: a gesture the reader has not finished making. A press on
    /// anything else in the list is <see cref="Touched"/>, because it commits no selection to protect.
    /// </summary>
    Pressing,
}

/// <summary>What a page does with a selection change its list reported. See <see cref="ListGestures.Judge"/>.</summary>
public enum ListReport
{
    /// <summary>The reader's own gesture: it becomes the selection.</summary>
    Accept,

    /// <summary>
    /// The page's own write, or the rows being rewritten under it. Nothing to do: the write ends by
    /// putting the highlight where the selection says.
    /// </summary>
    Ignore,

    /// <summary>
    /// The control highlighted a row on its own. Put the highlight back on the selection, later rather
    /// than inside the control's report, and only if the reader has still not touched the list by then
    /// (<see cref="ListGestures.StillUntouched"/>).
    /// </summary>
    Restore,
}

/// <summary>
/// Whether a selection a list reports is the reader's gesture, for a page that rewrites the rows under
/// the list while the reader may be using it: Explore as a scan publishes, Memory as it reads the
/// machine every couple of seconds.
///
/// <para>§7.1 says Explore never pre-selects, and §7.2 says the same of Memory. A <c>ListView</c>
/// reports a selection change for every row it drops from a rewrite, for every step of a page's own
/// write of the highlight, and for a row it highlights of its own accord as it settles on rows or
/// containers it was given, during a later layout pass and after the page has stopped writing. Taken
/// for a gesture, any of those leaves something picked that nobody picked, with Delete or the close
/// pointed at it. On a rescan, the rows a rewrite drops are node numbers of the tree before it, which
/// the arriving one cannot place at all.</para>
///
/// <para>The control's own write was reported against a navigation into a folder holding one entry,
/// where it puts a focused index back on the only row there is. It could not be provoked on demand
/// under driven input, so what was measured is the page's response to such a write rather than the
/// control making one.</para>
///
/// <para>So the question is not a window in time. It is whether the reader has touched the list since
/// it last settled, which has an answer however late the report arrives. Both gestures that move a
/// selection are marked as they arrive and before the control acts on them, so a genuine click or
/// arrow key counts on the first press after a rewrite rather than the second. A selection made
/// straight through UI Automation carries neither and is refused with the rest: it cannot be told
/// from the control's own write, and the direction of §7.1 is to drop a pick nobody can attribute.
/// The keyboard and the pointer both reach every row.</para>
///
/// <para>A <c>ListView</c> commits a pointer selection on the <em>release</em>, so a rewrite landing
/// while a press is held would otherwise settle the list before the click was reported, and the click
/// would be refused. <see cref="ListGesture.Pressing"/> is what keeps a settle from doing that.</para>
/// </summary>
public static class ListGestures
{
    /// <summary>
    /// A pointer went down in the list, on a row or not. Answered afresh, whatever the list stood at,
    /// so nothing an earlier press left can outlive this one.
    /// </summary>
    public static ListGesture Pressed(bool onRow) =>
        onRow ? ListGesture.Pressing : ListGesture.Touched;

    /// <summary>A key went down in the list. A press held on a row stays held.</summary>
    public static ListGesture Keyed(this ListGesture gesture) =>
        gesture == ListGesture.Pressing ? ListGesture.Pressing : ListGesture.Touched;

    /// <summary>
    /// A press ended, by a release, a capture loss or a cancellation. The reader's touch is kept: the
    /// list commits the click on the release, so the report the page is waiting for arrives after
    /// this.
    /// </summary>
    public static ListGesture Released(this ListGesture gesture) =>
        gesture == ListGesture.Pressing ? ListGesture.Touched : gesture;

    /// <summary>
    /// The list settled on rows or containers it was given, so whatever it reports next is its own
    /// doing, unless a press on a row is still held.
    /// </summary>
    public static ListGesture Settle(this ListGesture gesture) =>
        gesture == ListGesture.Pressing ? ListGesture.Pressing : ListGesture.Settled;

    /// <summary>
    /// What to do with a selection change the list reported.
    /// </summary>
    /// <param name="gesture">Where the list stands as the report arrives.</param>
    /// <param name="writing">
    /// Whether the page is writing the list's rows or its highlight, or the view-model is rewriting
    /// the rows, as the report arrives.
    /// </param>
    public static ListReport Judge(this ListGesture gesture, bool writing) =>
        writing ? ListReport.Ignore
        : gesture == ListGesture.Settled ? ListReport.Restore
        : ListReport.Accept;

    /// <summary>
    /// Whether a <see cref="ListReport.Restore"/> still applies when it runs. A press can land between
    /// the report and the restore and take the list back, and the restore would then drop that
    /// gesture.
    /// </summary>
    public static bool StillUntouched(this ListGesture gesture) => gesture == ListGesture.Settled;
}
