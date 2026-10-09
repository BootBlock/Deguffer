using Deguffer.Core.Cloud;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// One duplicate search as the page runs it (§7.4): what this machine protects asked afresh, the
/// search passing over what that refuses, and the marks made the moment the candidates are found, so
/// each group arrives with its place among the others already decided.
///
/// <para><b>Why the marks come before the first group.</b> Groups are sorted by the space each could
/// free, which only the keeping rule can say: a reference copy, a refused copy and a copy that cannot
/// be kept count for nothing. The rule needs the program folders the search reads as it finds the
/// candidates, so it is read then, and a group confirmed after it is placed where it belongs rather
/// than moved there later, which would take the reader's place in the list with it.</para>
///
/// <para>Orchestration only, as <see cref="DuplicateSearcher"/> is: the searcher matches, the marks
/// decide what can be kept and where a group goes, and this holds neither rule.</para>
/// </summary>
public sealed class DuplicateSearchRun
{
    private readonly DuplicateSearcher _searcher;
    private readonly Func<CancellationToken, Task<MachineProtections>> _protections;
    private readonly IUserEnvironment _environment;
    private readonly ICloudFiles _cloud;
    private readonly IVolumeInventory _volumes;
    private readonly VolumeMediaCache _media;

    /// <param name="protections">
    /// What this machine protects, built afresh for each search, as <see cref="MachineProtections.ForThisMachineAsync"/> does.
    /// </param>
    /// <param name="media">The cache the searcher reads the drives through, whose answers the marks take.</param>
    public DuplicateSearchRun(
        DuplicateSearcher searcher,
        Func<CancellationToken, Task<MachineProtections>> protections,
        IUserEnvironment environment,
        ICloudFiles cloud,
        IVolumeInventory volumes,
        VolumeMediaCache media)
    {
        _searcher = searcher;
        _protections = protections;
        _environment = environment;
        _cloud = cloud;
        _volumes = volumes;
        _media = media;
    }

    /// <summary>
    /// Search, handing over the marks once the candidates are found and each group as it is confirmed.
    /// </summary>
    /// <param name="marksMade">
    /// Given the marks once, off the UI thread, before the first group is given to
    /// <paramref name="found"/>, with what finding the candidates found as their
    /// <see cref="DuplicateMarks.UnsearchedReferences"/> and their keeping rule. No group has been
    /// added to them: the caller adds each as <paramref name="found"/> is given it, on the thread that
    /// reads them.
    /// </param>
    /// <param name="finding">Given what finding the candidates found, once, before the first group.</param>
    /// <exception cref="OperationCanceledException">The search was stopped before its candidates were all found.</exception>
    public async Task<DuplicateSearchResult> RunAsync(
        DuplicateSearch search,
        Action<DuplicateMarks> marksMade,
        IProgress<CandidateFinding>? finding,
        IProgress<DuplicateGroup>? found,
        IProgress<DuplicateSearchProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(marksMade);

        var protections = await _protections(ct).ConfigureAwait(false);

        // Started now, while the candidates are found, and kept by the protections for the marks to
        // read then: asking every provider where its clean deletes runs the tools that report their
        // caches, which can take as long as finding does.
        _ = protections.StorageCleansAsync(ct);

        return await _searcher.SearchAsync(
            search,
            protections.Policy,
            found,
            progress,
            async (candidates, token) =>
            {
                marksMade(await DuplicateMarks.ForAsync(candidates, protections, _environment, _cloud, _volumes, _media, token)
                    .ConfigureAwait(false));
                finding?.Report(candidates);
            },
            ct).ConfigureAwait(false);
    }
}
