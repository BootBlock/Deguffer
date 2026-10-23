using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;

namespace Deguffer.App.Tests;

/// <summary>
/// A compositor on a thread with a dispatcher queue, as the window's is, for a test of what the shell
/// draws through composition. One for the test run, made the first time it is asked for and never shut
/// down, because the map's device is the application's one and holds the first compositor it is given.
///
/// <para>The Windows App SDK's classes are found through the app's manifest, which names the library
/// each is in. The test host has none of its own, so the app's, embedded in Deguffer.App.exe beside the
/// tests, is made the thread's activation context before the first of them is asked for.</para>
/// </summary>
internal static class CompositionThread
{
    private static readonly Lazy<(DispatcherQueue Queue, Compositor Compositor)> Shared = new(Start);

    public static Compositor Compositor => Shared.Value.Compositor;

    /// <summary>Run <paramref name="work"/> on the compositor's thread, and return what it returns or rethrow what it threw.</summary>
    public static T Run<T>(Func<T> work) => Run(Shared.Value.Queue, work);

    private static (DispatcherQueue, Compositor) Start()
    {
        ActivateAppManifest();

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

    private static void ActivateAppManifest()
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

        if (handle == InvalidHandle || !ActivateActCtx(handle, out _))
        {
            throw new InvalidOperationException(
                $"The app's manifest could not be made the activation context: error {Marshal.GetLastPInvokeError()}.");
        }
    }

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
