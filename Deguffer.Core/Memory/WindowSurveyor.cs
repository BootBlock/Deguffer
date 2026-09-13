namespace Deguffer.Core.Memory;

/// <summary>What the top-level windows of one process say about it.</summary>
/// <param name="OwnsConsoleWindow">
/// Whether one of them is a console's, whatever its state, which refuses the whole process.
/// </param>
/// <param name="Qualifying">
/// Those that are unowned, visible and not cloaked, or null where the set could not be completed.
/// </param>
internal readonly record struct WindowSurvey(Answer OwnsConsoleWindow, IReadOnlyList<ProcessWindow>? Qualifying);

/// <summary>
/// Which of one process's top-level windows §7.2.1 would have a close posted to, and whether any of
/// them is a console's.
///
/// <para><b>A console window is recognised by its class, and that is not a documented contract.</b>
/// Both names come from the terminal's own source rather than from Microsoft's documentation, and
/// §7.2.1 states the residual risk that carries: were a class name to change, Deguffer would stop
/// recognising that console. There is no documented call that asks whether a window belongs to a
/// console, and the alternative — attaching to another program's console to ask — is worse than the
/// risk.</para>
///
/// <para><b>The console check looks at every top-level window of the process, visible or not, owned or
/// not.</b> A graphical program can own a console window: one that is not a console program owned an
/// invisible one on a workstation checked on 2026-09-13, and whether a console's window is visible
/// says nothing about which programs share the console.</para>
///
/// <para><b>A window that has gone since the enumeration is skipped, and a window that is still there
/// and will not answer makes the fact unreadable.</b> Windows are created and destroyed constantly, so
/// reading a destroyed one as an unreadable fact would refuse a process for a race rather than for a
/// reason.</para>
/// </summary>
internal static class WindowSurveyor
{
    private const string ConsoleWindowClass = "ConsoleWindowClass";
    private const string PseudoConsoleWindowClass = "PseudoConsoleWindow";

    public static WindowSurvey Take(IWindowCalls calls, int processId)
    {
        if (calls.TopLevel() is not { } windows)
        {
            return new WindowSurvey(Answer.Unreadable, Qualifying: null);
        }

        var qualifying = new List<ProcessWindow>();
        var consoleUnreadable = false;
        var qualifyingUnreadable = false;

        foreach (var window in windows)
        {
            var owner = calls.ProcessOf(window);

            if (owner is null)
            {
                // Windows answers nothing here for a handle that is no longer a window, which is the
                // ordinary race. One that is still there and will not say whose it is could be the
                // console window that refuses the whole process, so it costs both facts.
                if (calls.Exists(window))
                {
                    consoleUnreadable = true;
                    qualifyingUnreadable = true;
                }

                continue;
            }

            if (owner != processId)
            {
                continue;
            }

            if (calls.ClassOf(window) is not { } name)
            {
                if (calls.Exists(window))
                {
                    // The class decides both facts, so a window still there that will not answer costs both.
                    consoleUnreadable = true;
                    qualifyingUnreadable = true;
                }

                continue;
            }

            if (name is ConsoleWindowClass or PseudoConsoleWindowClass)
            {
                // The whole process is refused, so the rest of the set is not worth completing.
                return new WindowSurvey(Answer.Yes, Qualifying: null);
            }

            switch (Qualifies(calls, window))
            {
                case true:
                    qualifying.Add(new ProcessWindow(window, name));
                    break;

                case null:
                    qualifyingUnreadable |= calls.Exists(window);
                    break;
            }
        }

        return new WindowSurvey(
            consoleUnreadable ? Answer.Unreadable : Answer.No,
            qualifyingUnreadable ? null : qualifying);
    }

    /// <summary>
    /// Whether §7.2.1 would post to it: unowned, because closing a dialog is not closing the program;
    /// visible and not cloaked, because a user whose program sits on another virtual desktop is told
    /// that rather than left with a close that appears to do nothing.
    /// </summary>
    private static bool? Qualifies(IWindowCalls calls, nint window)
    {
        if (calls.IsOwned(window) is not { } owned)
        {
            return null;
        }

        if (owned || !calls.IsVisible(window))
        {
            return false;
        }

        return calls.IsCloaked(window) is { } cloaked ? !cloaked : null;
    }
}
