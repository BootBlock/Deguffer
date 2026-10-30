using Deguffer.Core.Execution;
using Deguffer.Core.Providers;

namespace Deguffer.Testing;

/// <summary>
/// The app's own provider list, for a test that asks what is shipped rather than what a provider
/// finds. Built over an invented machine in a scratch tree, because over this one every provider would
/// share this machine's scanner and size cache, which a test process refuses
/// (<see cref="TestProcess.Isolate"/>).
/// </summary>
public sealed class ShippedProviders : IDisposable
{
    private readonly TempDirectory _root = new();

    public ShippedProviders()
    {
        Planner = CleanupPlanner.CreateDefault(environment: new FakeUserEnvironment(_root.Path));
    }

    /// <summary>The planner <see cref="CleanupPlanner.CreateDefault"/> composes, as the app's is.</summary>
    public CleanupPlanner Planner { get; }

    public IReadOnlyList<ICleanupProvider> Providers => Planner.Providers;

    public void Dispose() => _root.Dispose();
}
