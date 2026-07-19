using System.Reflection;
using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;
using Xunit.Sdk;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="Execution.CleanupPlanner.PlanAllAsync"/> invalidates every provider before planning, so a
/// rescan measures where a tool keeps its cache now rather than where it kept it last time. A
/// provider that memoises a resolved location has to clear it for that to hold, and the base
/// <see cref="CleanupProviderBase.InvalidateCaches"/> only clears the shared collaborators.
///
/// npm and NuGet both memoised a location and did not clear it, and nothing failed — the defect was
/// found by reading the providers side by side. These tests hold the invariant over every provider
/// that exists rather than over the ones someone thought to write a test for, so the next
/// <c>_resolvedX</c> field is caught by construction.
/// </summary>
public sealed class ProviderInvalidationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public ProviderInvalidationTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Mutable state on a provider is memoised machine knowledge — every field here today holds a
    /// location resolved by asking the tool. Anything the provider must keep across a rescan belongs
    /// in a readonly field set at construction, so "mutable" is a sound stand-in for "memoised", and
    /// a future field that genuinely has to survive should have to argue for itself here.
    /// </summary>
    [Fact]
    public void EveryProviderClearsItsMemoisedStateOnInvalidation()
    {
        foreach (var type in ProviderTypes())
        {
            var provider = Construct(type);
            var fields = MemoisedFields(type);

            foreach (var field in fields)
            {
                field.SetValue(provider, Sentinel(field));
            }

            provider.InvalidateCaches();

            foreach (var field in fields)
            {
                var remaining = field.GetValue(provider);

                Assert.True(
                    remaining is null,
                    $"{type.Name}.{field.Name} survived InvalidateCaches, so a rescan would reuse a " +
                    "location the tool may have stopped using. Clear it in an InvalidateCaches override.");
            }
        }
    }

    /// <summary>
    /// Every provider in the assembly, not only the registered ones: a provider is written before
    /// it is registered, and that is the window in which the field gets added.
    /// </summary>
    private static IReadOnlyList<Type> ProviderTypes() =>
    [
        .. typeof(CleanupProviderBase).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false } && t.IsSubclassOf(typeof(CleanupProviderBase)))
            .OrderBy(t => t.Name, StringComparer.Ordinal),
    ];

    /// <summary>
    /// Everything below <see cref="CleanupProviderBase"/>, not just the concrete type: if the
    /// command-based providers are ever factored onto a shared intermediate base, the memoised
    /// location moves with them and stopping at the leaf would quietly stop checking anything.
    /// The base class's own state is excluded because the base override is what clears it.
    /// </summary>
    private static IReadOnlyList<FieldInfo> MemoisedFields(Type type)
    {
        var fields = new List<FieldInfo>();

        for (var level = type; level is not null && level != typeof(CleanupProviderBase); level = level.BaseType)
        {
            fields.AddRange(level
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => !f.IsInitOnly));
        }

        return fields;
    }

    /// <summary>
    /// Stands in for an answer the provider resolved by asking the tool. The value is never read,
    /// only observed to have been discarded, so any non-null of the right type will do.
    /// </summary>
    private static object Sentinel(FieldInfo field) =>
        field.FieldType == typeof(string) ? "sentinel"
        : field.FieldType.IsAssignableFrom(typeof(string[])) ? new[] { "sentinel" }
        : throw new XunitException(
            $"{field.DeclaringType?.Name}.{field.Name} is a {field.FieldType.Name}, which this test " +
            "cannot fabricate a value for. Extend Sentinel so the field is still covered.");

    /// <summary>
    /// Through the fakes, so this proves the invalidation rule without npm, NuGet or PlatformIO
    /// installed and without disturbing the real shared collaborators.
    /// </summary>
    private CleanupProviderBase Construct(Type type)
    {
        var constructors = type.GetConstructors();

        Assert.True(
            constructors.Length == 1,
            $"{type.Name} has {constructors.Length} constructors, so this test cannot tell which one " +
            "to build it with and its invalidation would go unchecked.");

        // Every parameter has to be something this test can supply from a fake. That is the property
        // worth holding rather than a fixed signature: a provider may legitimately need a dependency
        // of its own beyond the four shared collaborators — approved source roots are the first —
        // but one taking something that can only come from the real machine would quietly drop out
        // of this sweep, which is exactly how the npm and NuGet defect went unnoticed.
        var arguments = constructors[0].GetParameters().Select(p => Argument(type, p.ParameterType)).ToArray();

        return (CleanupProviderBase)constructors[0].Invoke(arguments);
    }

    private object Argument(Type provider, Type parameter) =>
        parameter == typeof(IUserEnvironment) ? _environment
        : parameter == typeof(IProcessRunner) ? new FakeProcessRunner()
        : parameter == typeof(IProcessInspector) ? FakeProcessInspector.NothingRunning
        : parameter == typeof(IDirectoryScanner)
            ? new DirectoryScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated))
        : parameter == typeof(SourceRootStore) ? new SourceRootStore(_environment)
        : throw new XunitException(
            $"{provider.Name} takes a {parameter.Name}, which this test cannot fabricate from a fake. " +
            "Extend Argument so the provider is still covered.");
}
