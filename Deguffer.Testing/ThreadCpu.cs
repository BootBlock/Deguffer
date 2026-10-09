using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Deguffer.Testing;

/// <summary>
/// The processor time the calling thread has used, for a test that compares how long two inputs
/// take. The clock counts the time a thread waits while other work runs, which on a shared machine
/// grows with the load rather than the code, and the process's time counts every other test the
/// runner has going at once; the thread's own time counts neither.
///
/// <para>Windows advances it in steps of about 16 ms, so a comparison sums readings until each side
/// spans many steps (<see cref="Growth"/>).</para>
/// </summary>
public static class ThreadCpu
{
    /// <summary>The processor time the calling thread has used so far, in kernel and user mode.</summary>
    public static TimeSpan Used()
    {
        if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return TimeSpan.FromTicks(kernel + user);
    }

    /// <summary>
    /// How much more one run of <paramref name="large"/> costs than <paramref name="times"/> runs of
    /// <paramref name="small"/>, where <paramref name="large"/> does <paramref name="times"/> times the
    /// work: about 1 where the cost grows in proportion, and about <paramref name="times"/> where it
    /// grows with the square. The two are run in turn until each side has used
    /// <paramref name="span"/>, so one step of the thread's clock is a small part of either.
    /// </summary>
    public static double Growth(Action small, Action large, int times, TimeSpan span)
    {
        // Once each, before any is timed, so first-use costs land on neither side.
        small();
        large();

        TimeSpan smallUsed = TimeSpan.Zero, largeUsed = TimeSpan.Zero;

        while (smallUsed < span || largeUsed < span)
        {
            var start = Used();

            for (var i = 0; i < times; i++)
            {
                small();
            }

            smallUsed += Used() - start;

            start = Used();
            large();
            largeUsed += Used() - start;
        }

        return largeUsed / smallUsed;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);
}
