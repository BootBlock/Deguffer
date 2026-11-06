using System.Diagnostics.Tracing;
using Deguffer.Core.Configuration;

namespace Deguffer.App.Diagnostics;

/// <summary>
/// The shell's milestones on the UI thread, as start and stop pairs a trace of the running app
/// times: launch to the first frame, a tool window reopened after it, each tree Explore shows, each new picture's first drawing on
/// screen, and each batch of duplicate groups the Duplicates page applies. How to record and read
/// them is in <c>docs/tracing-the-shell.md</c>.
///
/// <para>No payload is a string, and none ever may be (a test holds every event to numbers and
/// enums): a trace carries counts, causes and node totals, so one can be shared without naming a
/// file, a folder or a user.</para>
///
/// <para>Nothing listens unless a trace is recording, and an event then costs the check
/// <see cref="EventSource.WriteEvent(int)"/> makes of whether it is enabled. Work done only for a
/// trace, such as waiting for a frame, is guarded by <see cref="EventSource.IsEnabled()"/> at its
/// call site, so a session with no trace pays for none of it.</para>
///
/// <para>Each pair is named <c>XStart</c> and <c>XStop</c>, with consecutive identifiers and no task
/// or opcode given, so the runtime makes each pair a task of its own and nests the pairs a trace
/// sees inside each other as activities.</para>
/// </summary>
[EventSource(Name = "Deguffer-Shell")]
internal sealed class ShellEvents : EventSource
{
    private ShellEvents()
    {
    }

    public static ShellEvents Log { get; } = new();

    /// <summary>The application is launched, before its window is built.</summary>
    [Event(1, Level = EventLevel.Informational)]
    public void LaunchStart() => WriteEvent(1);

    /// <summary>The window's first frame after it was activated has been handed to the compositor.</summary>
    [Event(2, Level = EventLevel.Informational)]
    public void LaunchStop() => WriteEvent(2);

    /// <summary>The main window is about to be built, with the page it opens on.</summary>
    [Event(3, Level = EventLevel.Informational)]
    public void WindowStart() => WriteEvent(3);

    [Event(4, Level = EventLevel.Informational)]
    public void WindowStop() => WriteEvent(4);

    /// <summary>
    /// A tool window left open last session is about to open again, after the main window's first
    /// frame (see <see cref="Views.ToolWindowOpener.Reopen"/>), so outside the launch.
    /// </summary>
    [Event(5, Level = EventLevel.Informational)]
    public void ToolWindowsStart() => WriteEvent(5);

    [Event(6, Level = EventLevel.Informational)]
    public void ToolWindowsStop() => WriteEvent(6);

    /// <summary>Explore is about to show a tree of <paramref name="nodes"/> nodes, for <paramref name="cause"/>.</summary>
    [Event(7, Level = EventLevel.Informational)]
    public void ShowStart(ShowCause cause, int nodes) => WriteEvent(7, (int)cause, nodes);

    /// <summary>Explore showed the tree, with <paramref name="rows"/> rows in its list.</summary>
    [Event(8, Level = EventLevel.Informational)]
    public void ShowStop(ShowCause cause, int rows) => WriteEvent(8, (int)cause, rows);

    /// <summary>Within a show, what is picked is about to be carried to the tree or cleared.</summary>
    [Event(9, Level = EventLevel.Informational)]
    public void SelectionStart() => WriteEvent(9);

    [Event(10, Level = EventLevel.Informational)]
    public void SelectionStop() => WriteEvent(10);

    /// <summary>Within a show, the rows are about to be brought up to date or rebuilt.</summary>
    [Event(11, Level = EventLevel.Informational)]
    public void RowsStart() => WriteEvent(11);

    [Event(12, Level = EventLevel.Informational)]
    public void RowsStop(int rows) => WriteEvent(12, rows);

    /// <summary>Within a show, the page is about to hand the tree to the map.</summary>
    [Event(13, Level = EventLevel.Informational)]
    public void HandoffStart() => WriteEvent(13);

    [Event(14, Level = EventLevel.Informational)]
    public void HandoffStop() => WriteEvent(14);

    /// <summary>The map was handed new picture number <paramref name="picture"/>, in <paramref name="view"/>.</summary>
    [Event(15, Level = EventLevel.Informational)]
    public void PictureStart(int picture, ExploreView view) => WriteEvent(15, picture, (int)view);

    /// <summary>
    /// Picture number <paramref name="picture"/> stopped waiting for its first drawing: one reached the
    /// screen, or another picture replaced it first. See <see cref="PictureOutcome"/>.
    /// </summary>
    [Event(16, Level = EventLevel.Informational)]
    public void PictureStop(int picture, PictureOutcome outcome) => WriteEvent(16, picture, (int)outcome);

    /// <summary>The Duplicates page is about to apply a batch of confirmed groups.</summary>
    [Event(17, Level = EventLevel.Informational)]
    public void GroupsStart() => WriteEvent(17);

    /// <summary>The batch is applied: <paramref name="groups"/> groups, and the figures over them once.</summary>
    [Event(18, Level = EventLevel.Informational)]
    public void GroupsStop(int groups) => WriteEvent(18, groups);
}
