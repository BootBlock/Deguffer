using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// What a test process shuts itself out of before its first test runs. Each test assembly calls
/// <see cref="Isolate"/> from its module initialiser, which runs as the test host loads the assembly,
/// so no test, and no order the tests run in, gets there first.
/// </summary>
public static class TestProcess
{
    /// <summary>
    /// Closes the scanner and size cache of the machine the suite runs on
    /// (<see cref="MachineScanFence"/>). A test measures the machine its
    /// <see cref="FakeUserEnvironment"/> describes, and one that reaches this machine's instead throws
    /// there rather than writing to the real user's profile.
    /// </summary>
    public static void Isolate() => MachineScanFence.Close();
}
