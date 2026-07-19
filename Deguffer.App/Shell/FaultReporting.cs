using Deguffer.Core.Diagnostics;
using Microsoft.UI.Xaml;

namespace Deguffer.App.Shell;

/// <summary>
/// Routes the exceptions nothing else catches into <see cref="CrashLog"/>.
///
/// A XAML callback that lets an exception escape takes the process down with stop code
/// <c>0xc000027b</c>, and the Application log records only <c>Microsoft.UI.Xaml.dll</c> as the
/// faulting module — the stack is gone by the time it is written. These three handlers are the
/// last point at which the detail still exists.
/// </summary>
public static class FaultReporting
{
    public static void Attach(Application application, CrashLog log)
    {
        // Fires for exceptions raised inside a framework-invoked callback on the UI thread — an
        // event handler or a binding — which is where 0xc000027b comes from. Handled is
        // deliberately left false: an exception from an unknown point in the XAML tree leaves the
        // app in an unknown state, and this tool deletes directories. Terminating is the right
        // outcome; terminating silently is not.
        //
        // It does *not* cover a throw from inside DispatcherQueue.TryEnqueue — measured, not
        // assumed: that one fails fast across the WinRT boundary without reaching any managed
        // handler, and leaves nothing behind. Nothing in the app enqueues today; anything that
        // starts to must catch at the callback rather than rely on this.
        application.UnhandledException += (_, e) => log.Record("Application.UnhandledException", e.Exception);

        // A faulted Task nobody awaited. The runtime ignores these rather than terminating, so
        // without a record the failure is invisible in both directions — no crash and no result.
        TaskScheduler.UnobservedTaskException += (_, e) =>
            log.Record("TaskScheduler.UnobservedTaskException", e.Exception);

        // The scan fans out across worker threads (G4). An exception on one of those never reaches
        // the XAML handler above.
        //
        // ExceptionObject is typed as object because a non-CLS payload can be thrown; recording
        // the entry with no detail still distinguishes "the handler never ran" from "the payload
        // was not an Exception", which an empty file does not.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Record("AppDomain.UnhandledException", e.ExceptionObject as Exception);
    }
}
