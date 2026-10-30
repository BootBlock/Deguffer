using System.Runtime.CompilerServices;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>Runs as the test host loads this assembly, before any test can run. See <see cref="TestProcess"/>.</summary>
internal static class TestProcessStart
{
    // CA2255 warns that a library's initialiser runs in whatever process loads it. A test assembly is
    // loaded only by a test host, which is the process this is meant to shut out.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Isolate() => TestProcess.Isolate();
#pragma warning restore CA2255
}
