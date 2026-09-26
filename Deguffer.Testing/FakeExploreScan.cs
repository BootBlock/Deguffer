using Deguffer.Core.Exploring;

namespace Deguffer.Testing;

/// <summary>One scan in progress on a <see cref="FakeExploreScanner"/>.</summary>
public sealed class FakeExploreScan
{
    private readonly IProgress<ExploreProgress>? _progress;

    // Continuations run asynchronously, so a scan finished from a test's own thread resumes the page
    // through its synchronisation context, as a real scan finishing on the thread pool does.
    private readonly TaskCompletionSource<ExploreScan> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal FakeExploreScan(string root, IProgress<ExploreProgress>? progress, CancellationToken ct)
    {
        Root = root;
        _progress = progress;

        ct.Register(() => _result.TrySetCanceled(ct));
    }

    /// <summary>What the scan was pointed at.</summary>
    public string Root { get; }

    internal Task<ExploreScan> Result => _result.Task;

    /// <summary>Report running counts, and a snapshot where one is given, as a walk does on its clock.</summary>
    public void Report(ExploreProgress progress) => _progress?.Report(progress);

    public void Finish(ExploreScan scan) => _result.SetResult(scan);

    public void Fail(Exception failure) => _result.SetException(failure);
}
