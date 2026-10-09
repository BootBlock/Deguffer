using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// The second stage of a duplicate search (§7.4): identifies the files that share a group by what the
/// tree holds, and groups them again by what Windows says of each, so a file is one file however
/// many paths reached it, and the times compare to the tick.
///
/// <para><b>Only the files that may match are opened.</b> A drive holds millions of files and most
/// share nothing with another, so the tree's grouping decides which are identified. Where the time is
/// a criterion and the tree holds none for a file, that file is identified first and placed by the
/// minute Windows gives, so it can still join a file the tree did date.</para>
///
/// <para><b>What could not be identified is left out and counted</b>, a file Windows would not
/// describe apart from one that is gone, because a file taken for gone may be the only copy left.
/// A path that names a folder or a link by then is counted as gone, since the file the scan saw is
/// no longer there. A file empty when it is opened is left out as the walk would have left it, and
/// one that went online-only since the scan is left out of a content search.</para>
/// </summary>
internal sealed class CandidateIdentification
{
    private readonly FileInformation _files;
    private readonly MatchCriteria _criteria;
    private readonly Dictionary<FoundFile, IdentifiedFile?> _identified = [];
    private readonly Dictionary<FileIdentity, IReadOnlyList<string>> _names = [];

    private int _empty;
    private int _onlyInTheCloud;
    private int _gone;
    private int _unidentified;

    public CandidateIdentification(FileInformation files, MatchCriteria criteria)
    {
        _files = files;
        _criteria = criteria;
    }

    /// <summary>What this stage left out, to add to what the walk left out.</summary>
    public LeftOutFiles LeftOut => new(Links: 0, _empty, UnknownLength: 0, _onlyInTheCloud, _gone, _unidentified);

    public IReadOnlyList<CandidateGroup> Group(IReadOnlyList<FoundFile> found, CancellationToken ct)
    {
        var byTheTree = CandidateGrouping.ByTheTree(found, _criteria, file =>
            CandidateGrouping.TreeMinuteOf(file) ?? (Identify(file, ct) is { } identified
                ? CandidateGrouping.MinuteOf(identified.Description.Modified)
                : null));

        List<IdentifiedFile> identified = [];

        foreach (var group in byTheTree)
        {
            foreach (var file in group)
            {
                if (Identify(file, ct) is { } known)
                {
                    identified.Add(known);
                }
            }
        }

        return CandidateGrouping.ByTheFiles(identified, _criteria, NamesOf);
    }

    /// <summary>The file as Windows describes it now, once however often it is asked, or null where it is left out.</summary>
    private IdentifiedFile? Identify(FoundFile file, CancellationToken ct)
    {
        if (_identified.TryGetValue(file, out var known))
        {
            return known;
        }

        ct.ThrowIfCancellationRequested();

        return _identified[file] = Decide(file, _files.Describe(file.Tree.PathOf(file.Node), file.Route));
    }

    private IdentifiedFile? Decide(FoundFile file, FileReading reading)
    {
        switch (reading.Result)
        {
            case FileReadingResult.Gone:
                _gone++;
                return null;

            case FileReadingResult.Unreadable:
                _unidentified++;
                return null;
        }

        var description = reading.Description!;

        if (!description.IsFile)
        {
            _gone++;
            return null;
        }

        if (description.Length == 0)
        {
            _empty++;
            return null;
        }

        if (_criteria.ReadsContent() && StorageAttributes.Of(description.Attributes) is FileStorage.CloudOnly)
        {
            _onlyInTheCloud++;
            return null;
        }

        return new IdentifiedFile(file, description);
    }

    /// <summary>
    /// Every name of a file with several, listed once a file. Where Windows will not list them, the
    /// paths the search reached the file by stand in, so the file is never shown with none.
    /// </summary>
    private IReadOnlyList<string> NamesOf(IdentifiedFile file)
    {
        var identity = file.Description.Identity;

        if (!_names.TryGetValue(identity, out var names))
        {
            _names[identity] = names = FileInformation.NamesOf(file.Description.Path)
                ?? [.. _identified.Values
                    .Where(other => other?.Description.Identity == identity)
                    .Select(other => other!.Description.Path)
                    .Distinct(StringComparer.Ordinal)];
        }

        return names;
    }
}
