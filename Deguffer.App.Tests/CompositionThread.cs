using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Windows.Foundation;

namespace Deguffer.App.Tests;

/// <summary>
/// A compositor on a thread with a dispatcher queue, as the window's is, for a test of what the shell
/// draws through composition. One for the test run, made the first time it is asked for and never shut
/// down, so the tests that draw through it share the one device the compositor has, as the maps do.
///
/// <para>The Windows App SDK's classes are found through the app's manifest, which names the library
/// each is in. The test host has none of its own, so the app's, embedded in Deguffer.App.exe beside the
/// tests, is made the activation context of every thread that hands the compositor work, before its
/// first piece.</para>
///
/// <para>Of every such thread, not only the one that started the compositor: a test that handed it work
/// from another thread, as a test class run beside the first does, or an async test resumed on one,
/// left the map's device unable to make its <c>CompositionCapabilities</c> ("ClassFactory cannot
/// supply requested class") for every test after it.</para>
/// </summary>
internal static class CompositionThread
{
    private static readonly Lazy<(DispatcherQueue Queue, Compositor Compositor)> Shared = new(Start);

    /// <summary>Whether this thread has the app's manifest as its activation context.</summary>
    [ThreadStatic]
    private static bool _activated;

    public static Compositor Compositor => Shared.Value.Compositor;

    /// <summary>The dispatcher queue of the compositor's thread, for a timer that runs on it.</summary>
    public static DispatcherQueue Queue => Shared.Value.Queue;

    /// <summary>Run <paramref name="work"/> on the compositor's thread, and return what it returns or rethrow what it threw.</summary>
    public static T Run<T>(Func<T> work)
    {
        // Queued behind the work doing the asking, so waiting for it here would wait forever.
        if (Shared.Value.Queue.HasThreadAccess)
        {
            throw new InvalidOperationException("Work was handed to the compositor's thread from that thread, where waiting for it never ends.");
        }

        Activate();

        return Run(Shared.Value.Queue, work);
    }

    /// <summary>
    /// Ask the compositor for a commit, and finish once it has made it and the compositor's thread has
    /// run what the commit handed it, on the thread pool.
    ///
    /// <para>What hears the commit is attached on the compositor's thread, in the piece of work that asks
    /// for it, because the commit is made only once that work has returned. A handler attached from
    /// another thread, through <c>AsTask</c>, can miss the completion, and a test awaiting it then never
    /// finishes.</para>
    ///
    /// <para>What the commit handed the thread, such as an interaction tracker's reports to its owner,
    /// runs before this finishes, because each report holds its owner while it runs, and a test that
    /// asks whether the owner can be collected must not ask while one is running. The finish is queued
    /// behind that work at a lower priority.</para>
    ///
    /// <para>The test resumes on the thread pool rather than inside the compositor's callback: a test that
    /// has already resumed once has no context to return to, and inside the callback its next
    /// <see cref="Run"/> would wait on the thread it is itself holding.</para>
    /// </summary>
    /// <exception cref="TimeoutException">The compositor did not report the commit within <see cref="CommitPatience"/>.</exception>
    public static Task CommitAsync()
    {
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Run(() =>
        {
            var commit = Compositor.RequestCommitAsync();
            commit.Completed = (action, status) =>
            {
                if (status != AsyncStatus.Completed)
                {
                    committed.TrySetException(new InvalidOperationException($"The compositor's commit ended {status}.", action.ErrorCode));
                }
                else if (!Queue.TryEnqueue(DispatcherQueuePriority.Low, () => committed.TrySetResult()))
                {
                    committed.TrySetException(new InvalidOperationException("The compositor's thread has stopped taking work."));
                }
            };

            return commit;
        });

        return committed.Task.WaitAsync(CommitPatience);
    }

    /// <summary>Far longer than a commit takes, so a commit not reported fails its test rather than holding the run.</summary>
    private static readonly TimeSpan CommitPatience = TimeSpan.FromSeconds(10);

    private static (DispatcherQueue, Compositor) Start()
    {
        Activate();

        // The thread takes the activation context of the one that starts it.
        var queue = DispatcherQueueController.CreateOnDedicatedThread().DispatcherQueue;

        return (queue, Run(queue, () => new Compositor()));
    }

    private static T Run<T>(DispatcherQueue queue, Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!queue.TryEnqueue(() =>
        {
            // Every failure is handed to the test thread, where the test reports it.
            try
            {
                done.SetResult(work());
            }
            catch (Exception exception)
            {
                done.SetException(exception);
            }
        }))
        {
            throw new InvalidOperationException("The compositor's thread has stopped taking work.");
        }

        return done.Task.GetAwaiter().GetResult();
    }

    /// <summary>Make the app's manifest this thread's activation context, unless it is already.</summary>
    private static void Activate()
    {
        if (_activated)
        {
            return;
        }

        if (!ActivateActCtx(AppManifest.Value, out _))
        {
            throw new InvalidOperationException(
                $"The app's manifest could not be made the activation context: error {Marshal.GetLastPInvokeError()}.");
        }

        _activated = true;
    }

    /// <summary>The activation context of the app's manifest, made once and activated on each thread that needs it.</summary>
    private static readonly Lazy<nint> AppManifest = new(() =>
    {
        var context = new ActivationContext
        {
            Size = Marshal.SizeOf<ActivationContext>(),
            Flags = ResourceNameValid | AssemblyDirectoryValid,
            Source = Path.Combine(AppContext.BaseDirectory, "Deguffer.App.exe"),
            AssemblyDirectory = AppContext.BaseDirectory,
            ResourceName = ManifestResource,
        };

        var handle = CreateActCtxW(ref context);

        return handle != InvalidHandle
            ? handle
            : throw new InvalidOperationException(
                $"The app's manifest could not be read as an activation context: error {Marshal.GetLastPInvokeError()}.");
    });

    private const uint AssemblyDirectoryValid = 0x004;

    private const uint ResourceNameValid = 0x008;

    /// <summary>The resource an executable's own manifest is embedded as.</summary>
    private const nint ManifestResource = 1;

    private static readonly nint InvalidHandle = -1;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateActCtxW(ref ActivationContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ActivateActCtx(nint context, out nint cookie);

    /// <summary>ACTCTXW.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ActivationContext
    {
        public int Size;
        public uint Flags;
        public string Source;
        public ushort ProcessorArchitecture;
        public ushort LanguageId;
        public string? AssemblyDirectory;
        public nint ResourceName;
        public string? ApplicationName;
        public nint Module;
    }
}
