using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// Whether this process may measure the machine it runs on through what every
/// <see cref="UserEnvironment"/> shares: <see cref="DirectoryScanner.For"/>'s scanner of its volumes
/// and the size cache in its account's application data.
///
/// <para>Open in the app. A test process closes it as its assembly loads, so a test that reaches this
/// machine's scanner fails where it reaches it. Without the fence such a test passes, and on the way
/// writes its scratch paths into the real user's <c>scan-estimates.json</c> from a copy loaded at its
/// start, taking away whatever a Deguffer running beside it set, and shares one volume index with
/// every parallel test that invalidates it.</para>
/// </summary>
internal static class MachineScanFence
{
    private static volatile bool _closed;

    /// <summary>For the life of the process: nothing a test does may reopen it.</summary>
    internal static void Close() => _closed = true;

    /// <exception cref="InvalidOperationException">The fence is closed.</exception>
    internal static void Pass()
    {
        if (_closed)
        {
            throw new InvalidOperationException(
                "This process does not measure the machine it runs on. Build the scanner, the size cache or the planner over the "
                + "environment the test describes, such as a FakeUserEnvironment, rather than over UserEnvironment.Current.");
        }
    }
}
