using Deguffer.Core.Duplicates;

namespace Deguffer.App.ViewModels;

/// <summary>
/// Runs one duplicate search as the page asks it, handing over the marks once the candidates are
/// found and each group as it is confirmed. See <see cref="DuplicateSearchRun.RunAsync"/>.
/// </summary>
public delegate Task<DuplicateSearchResult> RunDuplicateSearch(
    DuplicateSearch search,
    Action<DuplicateMarks> marksMade,
    IProgress<CandidateFinding> finding,
    IProgress<DuplicateGroup> found,
    IProgress<DuplicateSearchProgress> progress,
    CancellationToken ct);
