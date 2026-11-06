using System.Diagnostics.Tracing;
using System.Reflection;
using Deguffer.App.Diagnostics;

namespace Deguffer.App.Tests;

/// <summary>
/// What a trace of the shell can hold: events the runtime accepts, and payloads of numbers and enums
/// only, so a trace never names a file, a folder or a user.
/// </summary>
public sealed class ShellEventsTests
{
    /// <summary>
    /// No event takes a string, or anything else that could carry a name. A rule on every event, not
    /// one test per event, so an event added later with a path in its payload fails here.
    /// </summary>
    [Fact]
    public void EveryEventCarriesOnlyNumbersAndEnums()
    {
        var events = typeof(ShellEvents)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<EventAttribute>() is not null)
            .ToList();

        Assert.NotEmpty(events);
        Assert.All(
            events.SelectMany(method => method.GetParameters()),
            parameter => Assert.True(
                parameter.ParameterType == typeof(int) || parameter.ParameterType.IsEnum,
                $"{parameter.Member.Name}({parameter.Name}) is a {parameter.ParameterType.Name}."));
    }

    /// <summary>
    /// The runtime accepts every event. A source it rejects raises nothing and reports nothing, so a
    /// mismatched identifier or a pair it cannot nest would otherwise go unseen until a trace came back
    /// empty. Strict generation checks each <c>WriteEvent</c> call against its event's identifier.
    /// </summary>
    [Fact]
    public void TheRuntimeAcceptsEveryEvent()
    {
        var manifest = EventSource.GenerateManifest(typeof(ShellEvents), typeof(ShellEvents).Assembly.Location, EventManifestOptions.Strict);

        Assert.NotNull(manifest);
        Assert.Contains("name=\"Deguffer-Shell\"", manifest, StringComparison.Ordinal);
        Assert.Contains("opcode=\"win:Start\"", manifest, StringComparison.Ordinal);
        Assert.Contains("opcode=\"win:Stop\"", manifest, StringComparison.Ordinal);
    }
}
