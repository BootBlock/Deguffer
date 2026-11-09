namespace Deguffer.App.Tests;

/// <summary>Whether the collector takes an object that nothing else holds.</summary>
internal static class Collector
{
    /// <summary>
    /// Collect until <paramref name="reference"/> is let go, running finalisers between: a native
    /// object's wrapper releases what it holds only from its finaliser, and what that held is collected
    /// in a later pass.
    /// </summary>
    /// <returns>False if it is still alive after every pass.</returns>
    public static bool Reclaims(WeakReference reference)
    {
        for (var pass = 0; pass < 10 && reference.IsAlive; pass++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return !reference.IsAlive;
    }
}
