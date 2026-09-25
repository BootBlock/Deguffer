using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether any process of the named programs is running, asked again immediately before a removal the
/// plan offered because none was.
///
/// <para>For an offer whose only evidence is a program's name: a folder that program reuses under a
/// fixed name, or a store its server keeps open while it runs. The name says nothing about which
/// session of the program, so any one of them holds the step back, as it would have kept the step out
/// of the plan.</para>
/// </summary>
/// <param name="names">Process names without extension, compared as <see cref="IProcessInspector.FindRunning"/> compares them.</param>
internal sealed class RunningProcessCheck(IProcessInspector inspector, IReadOnlyList<string> names) : IUseCheck
{
    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
    {
        // The inspector keeps one snapshot for a planning pass. That snapshot is the preview's.
        inspector.Invalidate();

        return inspector.FindRunning(names) is { Count: > 0 } running
            ? [new InUseNow(step.Path, $"{string.Join(", ", running)} {(running.Count == 1 ? "is" : "are")} running now")]
            : [];
    }
}
