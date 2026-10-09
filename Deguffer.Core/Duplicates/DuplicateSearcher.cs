using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Duplicates;

/// <summary>What a duplicate search confirmed, and everything it did not look at or left out.</summary>
/// <param name="Finding">
/// What finding the candidates found and passed over: the locations not searched, the places passed
/// over or not read, and the install locations set aside.
/// </param>
/// <param name="Groups">Every group confirmed, in the order each was, including before a stop.</param>
/// <param name="LeftOut">Every file left out, by finding the candidates and by reading them.</param>
/// <param name="Stopped">
/// Whether the search was stopped while it read the files' content, so files may match that are in
/// no group. What it confirmed before the stop stands.
/// </param>
public sealed record DuplicateSearchResult(
    CandidateFinding Finding,
    IReadOnlyList<DuplicateGroup> Groups,
    LeftOutFiles LeftOut,
    bool Stopped);

/// <summary>
/// Runs a duplicate search from its locations to its confirmed groups (§7.4). Orchestration only:
/// <see cref="CandidateFinder"/> finds and identifies the files that may match, and
/// <see cref="ContentMatching"/> matches them by their bytes where the content is a criterion. Any
/// other search is confirmed on what finding compared, the name, the size or the time.
///
/// <para><b>Results stream.</b> Each group is given to the caller the moment it is confirmed, and
/// progress is reported by stage, so the page can fill its list while the search runs.</para>
///
/// <para><b>A stopped search keeps what it confirmed.</b> Stopped while reading content, the search
/// answers every group confirmed before the stop, marked as stopped, even where that is none: the
/// candidates were all found, so the places it did not search can still be named. Stopped before the
/// candidates were all found, it throws <see cref="OperationCanceledException"/>: nothing is
/// confirmed by then, and a partial list of what was passed over would read as the whole of it.</para>
/// </summary>
public sealed class DuplicateSearcher
{
    private readonly CandidateFinder _finder;
    private readonly VolumeMediaCache _media;
    private readonly ReadContent _read;
    private readonly ChecksumCache? _remembered;

    /// <param name="media">
    /// The disks behind each volume, and their kind, remembered for the life of the app, so files are
    /// read in lanes by disk.
    /// </param>
    /// <param name="remembered">
    /// The checksums earlier searches read, for the life of the app, used where a file is unchanged
    /// and stored once each search stops reading.
    /// </param>
    public DuplicateSearcher(CandidateFinder finder, VolumeMediaCache media, ChecksumCache remembered)
        : this(finder, media, ContentReader.Remembering(remembered).Read, remembered)
    {
    }

    /// <param name="read">
    /// Reads one file's content, so a test can count reads, hold them to see how many run at once, or
    /// stop the search partway.
    /// </param>
    /// <param name="remembered">The cache <paramref name="read"/> uses, stored once the search stops reading, or null where it uses none.</param>
    internal DuplicateSearcher(CandidateFinder finder, VolumeMediaCache media, ReadContent read, ChecksumCache? remembered = null)
    {
        _finder = finder;
        _media = media;
        _read = read;
        _remembered = remembered;
    }

    /// <param name="policy">Explore's policy for this machine, whose refusals the search passes over.</param>
    /// <param name="found">Given each group as it is confirmed, one at a time, from any thread.</param>
    /// <param name="candidatesFound">
    /// Awaited once with what finding the candidates found, before any content is read and before the
    /// first group is confirmed, so a page can read what decides each group's place in its list (the
    /// space it could free, which needs the program folders finding read) before the first one arrives.
    /// </param>
    /// <exception cref="ArgumentException">The search compares content by a checksum this machine does not offer.</exception>
    /// <exception cref="OperationCanceledException">The search was stopped before its candidates were all found.</exception>
    public async Task<DuplicateSearchResult> SearchAsync(
        DuplicateSearch search,
        ExploreActionPolicy policy,
        IProgress<DuplicateGroup>? found = null,
        IProgress<DuplicateSearchProgress>? progress = null,
        Func<CandidateFinding, CancellationToken, Task>? candidatesFound = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(search);

        var readsContent = search.Criteria.ReadsContent();

        if (readsContent && !ChecksumAlgorithms.IsOffered(search.Algorithm))
        {
            throw new ArgumentException("Windows on this computer does not provide that checksum.", nameof(search));
        }

        var finding = await _finder.FindAsync(search, policy, progress is null ? null : new FindingProgress(progress), ct)
            .ConfigureAwait(false);

        if (candidatesFound is not null)
        {
            await candidatesFound(finding, ct).ConfigureAwait(false);
        }

        List<DuplicateGroup> confirmed = [];

        void Confirm(DuplicateGroup group)
        {
            confirmed.Add(group);
            found?.Report(group);
        }

        if (!readsContent)
        {
            foreach (var group in finding.Groups)
            {
                Confirm(new DuplicateGroup(search.Criteria, group.Length, Checksum: null, group.Files));
            }

            return new DuplicateSearchResult(finding, confirmed, finding.LeftOut, Stopped: false);
        }

        var matching = new ContentMatching(_read, _media);
        var stopped = false;

        try
        {
            await matching.MatchAsync(finding.Groups, search.Criteria, search.Algorithm, Confirm, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller stopped the search: what was confirmed stands, and the result says it stopped.
            stopped = true;
        }
        finally
        {
            // Every reader has finished by now, stopped or not, and what each read is as true of its
            // file as it was, so a stopped search keeps its reads too.
            if (_remembered is not null)
            {
                await Task.Run(_remembered.Save, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return new DuplicateSearchResult(finding, confirmed, finding.LeftOut + matching.LeftOut, stopped);
    }

    /// <summary>The scan's progress, as the first stage of the search's.</summary>
    private sealed class FindingProgress(IProgress<DuplicateSearchProgress> progress) : IProgress<ExploreProgress>
    {
        public void Report(ExploreProgress value) =>
            progress.Report(new DuplicateSearchProgress(DuplicateSearchStage.Finding, 0, Total: null, value));
    }
}
