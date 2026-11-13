namespace Deguffer.Core.Duplicates;

/// <summary>The parts of a duplicate search that run one after another, in the order it runs them (§7.4).</summary>
public enum DuplicateSearchPart
{
    /// <summary>Reading the locations' trees: each volume's file table where it can be, and the walk where it cannot.</summary>
    Scan,

    /// <summary>Going through the trees and keeping the files that may match (<see cref="CandidateWalk"/>).</summary>
    Walk,

    /// <summary>
    /// Asking Windows which file each kept file is, and listing the names of each file with several
    /// (<see cref="CandidateIdentification"/>).
    /// </summary>
    Identification,

    /// <summary>
    /// Reading the files' content, the first and last blocks and the whole files beside each other,
    /// until the last group is confirmed (<see cref="ContentMatching"/>). Nothing for a search that
    /// compares no content.
    /// </summary>
    Comparison,
}

/// <summary>
/// How long each part of one duplicate search took, what each part read, and how long each lane of
/// its content comparison took. Handed to a search by whatever measures it, and null in the app, so
/// a search nobody measures pays a null check at each part and each read, and nothing else.
///
/// <para><b>The two stages of the comparison overlap</b> (<see cref="ContentMatching"/>), so each is
/// timed from its first read starting to its last ending, and the two can add up to more than
/// <see cref="DuplicateSearchPart.Comparison"/>. The parts themselves run one after another, so they
/// add up to the search less what it does between them, such as resolving its locations.</para>
///
/// <para>One instance times one search. A part the search stopped before reads as zero.</para>
/// </summary>
/// <param name="clock">What the parts and the reads are timed by. The system's where null.</param>
public sealed class DuplicateSearchTimings(TimeProvider? clock = null)
{
    private const int Parts = (int)DuplicateSearchPart.Comparison + 1;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly long[] _started = new long[Parts];
    private readonly TimeSpan[] _elapsed = new TimeSpan[Parts];

    private ContentLaneSpans? _lanes;

    /// <summary>How many files the walk kept as files that may match.</summary>
    public int Kept { get; internal set; }

    /// <summary>How many files identification asked Windows about, every pass together.</summary>
    public int Identified { get; internal set; }

    /// <summary>How many files with several names identification listed the names of.</summary>
    public int Named { get; internal set; }

    /// <summary>The first and last blocks read, every lane together.</summary>
    public ContentReadTally Ends => _lanes?.Tally(ContentPart.Ends) ?? default;

    /// <summary>The whole files read, every lane together.</summary>
    public ContentReadTally Whole => _lanes?.Tally(ContentPart.Whole) ?? default;

    /// <summary>Each lane the content was read in, one a disk, in the order the search made them. None where no content was compared.</summary>
    public IReadOnlyList<ContentLaneTimes> Lanes => _lanes?.Lanes() ?? [];

    /// <summary>How long <paramref name="part"/> took, or zero where it did not run.</summary>
    public TimeSpan Of(DuplicateSearchPart part) => _elapsed[(int)part];

    /// <summary>Start the clock for <paramref name="part"/>.</summary>
    internal void Start(DuplicateSearchPart part) => _started[(int)part] = _clock.GetTimestamp();

    /// <summary>Record that <paramref name="part"/> has ended.</summary>
    internal void End(DuplicateSearchPart part) => _elapsed[(int)part] = _clock.GetElapsedTime(_started[(int)part]);

    /// <summary>What records each read of the comparison, in lanes of <paramref name="readers"/>.</summary>
    internal ContentLaneSpans Comparing(IReadOnlyList<int> readers) => _lanes = new ContentLaneSpans(readers, _clock);
}
