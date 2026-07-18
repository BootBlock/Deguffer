using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>Declares which of a tool's processes are "running", for the §5.3 warning path.</summary>
public sealed class FakeProcessInspector(params string[] running) : IProcessInspector
{
    public static FakeProcessInspector NothingRunning => new();

    public int InvalidateCount { get; private set; }

    public IReadOnlyList<string> FindRunning(IEnumerable<string> names) =>
        [.. names.Intersect(running, StringComparer.OrdinalIgnoreCase)];

    public void Invalidate() => InvalidateCount++;
}
