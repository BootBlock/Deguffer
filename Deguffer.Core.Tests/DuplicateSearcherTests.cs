using System.Security.Cryptography;
using Deguffer.Core.Duplicates;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A duplicate search from its locations to its confirmed groups (§7.4): what matches on content,
/// what a search that does not compare content opens, and what a stopped search keeps.
/// </summary>
public sealed class DuplicateSearcherTests : IDisposable
{
    private const int Block = ContentReader.BlockBytes;

    private readonly DuplicateTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private SearchLocation Searched(params string[] segments) => new(Path.Combine([_tree.Top, .. segments]));

    private static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// The true copies are one group with the checksum of the algorithm chosen, and a file of the
    /// same length with other bytes is in none.
    /// </summary>
    [Fact]
    public async Task AContentSearchFindsTheCopiesAndSplitsAFileOfTheSameLength()
    {
        var content = Random(5000, 1);
        _tree.File(content, "Data", "a.bin");
        _tree.File(content, "Data", "Other", "copy.bin");
        _tree.File(Random(5000, 2), "Data", "different.bin");

        var result = await _tree.Searcher().SearchAsync(
            new DuplicateSearch(MatchCriteria.Content, [Searched("Data")], ChecksumAlgorithm.Sha256), _tree.Policy());

        var group = Assert.Single(result.Groups);
        Assert.Equal(["a.bin", "copy.bin"], group.Files.Select(file => file.Name).Order());
        Assert.Equal(new ContentChecksum(ChecksumAlgorithm.Sha256, SHA256.HashData(content)), group.Checksum);
        Assert.Equal(5000, group.Length);
        Assert.False(result.Stopped);
    }

    /// <summary>
    /// Two files whose first and last blocks are the same and whose middles differ are not a match:
    /// the full content is read wherever the ends do not cover it. A third file, differing in its
    /// first block, is not read in full at all, because the first stage already parted it.
    /// </summary>
    [Fact]
    public async Task EqualFirstAndLastBlocksWithDifferentMiddlesDoNotMatch()
    {
        var content = Random(3 * Block, 3);
        var middle = (byte[])content.Clone();
        middle[Block + (Block / 2)] ^= 0xFF;
        var start = (byte[])content.Clone();
        start[0] ^= 0xFF;
        _tree.File(content, "Data", "a.bin");
        _tree.File(content, "Data", "copy.bin");
        _tree.File(middle, "Data", "middle.bin");
        _tree.File(start, "Data", "start.bin");
        List<string> readInFull = [];

        var result = await _tree.Searcher((file, part, checksum, ct) =>
        {
            if (part is ContentPart.Whole)
            {
                lock (readInFull)
                {
                    readInFull.Add(file.Name);
                }
            }

            return ContentReader.Default.Read(file, part, checksum, ct);
        }).SearchAsync(new DuplicateSearch(MatchCriteria.Content, [Searched("Data")]), _tree.Policy());

        Assert.Equal(["a.bin", "copy.bin"], Assert.Single(result.Groups).Files.Select(file => file.Name).Order());
        Assert.Equal(["a.bin", "copy.bin", "middle.bin"], readInFull.Order());
    }

    /// <summary>
    /// The candidates are handed over, and waited for, before any content is read and before the
    /// first group is confirmed, which is what lets a page read the keeping rule each group is
    /// placed by before the first one arrives.
    /// </summary>
    [Theory]
    [InlineData(MatchCriteria.Content)]
    [InlineData(MatchCriteria.Name)]
    public async Task TheCandidatesAreHandedOverBeforeAnythingIsReadOrConfirmed(MatchCriteria criteria)
    {
        var content = Random(5000, 6);
        _tree.File(content, "Data", "a.bin");
        _tree.File(content, "Data", "Other", "a.bin");
        List<string> happened = [];
        CandidateFinding? handed = null;

        void Happened(string step)
        {
            lock (happened)
            {
                happened.Add(step);
            }
        }

        var result = await _tree.Searcher((file, part, checksum, ct) =>
        {
            Happened("read");
            return ContentReader.Default.Read(file, part, checksum, ct);
        }).SearchAsync(
            new DuplicateSearch(criteria, [Searched("Data")]),
            _tree.Policy(),
            new CallbackProgress<DuplicateGroup>(_ => Happened("confirmed")),
            candidatesFound: async (finding, _) =>
            {
                // Held, so a search that did not wait for this would read or confirm meanwhile.
                await Task.Delay(50);
                handed = finding;
                Happened("handed over");
            });

        Assert.Same(result.Finding, handed);
        Assert.Equal("handed over", happened[0]);
        Assert.Equal(1, happened.Count(step => step == "handed over"));
        Assert.Contains("confirmed", happened);
    }

    /// <summary>A search on the name never opens a file's content, so a reader that fails if asked proves it.</summary>
    [Fact]
    public async Task ASearchThatDoesNotCompareContentConfirmsWithoutReadingAny()
    {
        _tree.File(Random(100, 4), "Data", "a.jpg");
        _tree.File(Random(200, 5), "Data", "Other", "A.JPG");
        List<DuplicateGroup> streamed = [];

        var result = await _tree.Searcher((_, _, _, _) => throw new InvalidOperationException("A name search read a file's content."))
            .SearchAsync(
                new DuplicateSearch(MatchCriteria.Name, [Searched("Data")]),
                _tree.Policy(),
                new CallbackProgress<DuplicateGroup>(streamed.Add));

        var group = Assert.Single(result.Groups);
        Assert.Null(group.Checksum);
        Assert.Null(group.Length);
        Assert.Equal(MatchCriteria.Name, group.Criteria);
        Assert.Equal([group], streamed);
    }

    /// <summary>
    /// Stopped while the first and last blocks are read, the search keeps the groups it confirmed and
    /// says it stopped. Each group of small files is confirmed as soon as both are read, and the
    /// drive's disks are unknown, so one file is read at a time: the stop comes as the second group's
    /// second file is read, after the first group was confirmed. The read it came during completes
    /// the second group, which is not confirmed, because the search was stopped by then. No read
    /// starts after it.
    /// </summary>
    [Fact]
    public async Task AStopWhileReadingBlocksKeepsWhatWasConfirmedAndStartsNoMoreReads()
    {
        foreach (var length in new[] { 4000, 3000, 2000, 1000 })
        {
            var content = Random(length, length);
            _tree.File(content, "Data", $"{length}-a.bin");
            _tree.File(content, "Data", $"{length}-b.bin");
        }

        var (result, readsAfterTheStop, confirmedAfterTheStop) = await StopAtRead(4, MatchCriteria.Content);

        Assert.True(result.Stopped);
        Assert.Equal([4000L], result.Groups.Select(group => group.Length!.Value));
        Assert.Equal(0, readsAfterTheStop);
        Assert.Equal(0, confirmedAfterTheStop);
    }

    /// <summary>
    /// Stopped while the full content is read, before any group is confirmed: the result is still a
    /// result, stopped and with no groups, because the candidates were all found and what the search
    /// passed over can still be named. No read starts after the stop.
    /// </summary>
    [Fact]
    public async Task AStopWhileReadingInFullBeforeAnyGroupIsConfirmedIsAStoppedResult()
    {
        foreach (var seed in new[] { 1, 2 })
        {
            var content = Random(3 * Block, seed);
            _tree.File(content, "Data", $"{seed}-a.bin");
            _tree.File(content, "Data", $"{seed}-b.bin");
        }

        // Four reads of the ends, then the first read in full.
        var (result, readsAfterTheStop, _) = await StopAtRead(5, MatchCriteria.Content);

        Assert.True(result.Stopped);
        Assert.Empty(result.Groups);
        Assert.Equal(0, readsAfterTheStop);
        Assert.NotEmpty(result.Finding.Read);
    }

    /// <summary>
    /// Stopped before the candidates were all found, nothing is confirmed and what was passed over is
    /// not all known, so the search throws rather than answer a partial account as a whole one.
    /// </summary>
    [Fact]
    public async Task AStopBeforeTheCandidatesAreFoundThrows()
    {
        _tree.File(100, "Data", "a.bin");
        _tree.File(100, "Data", "b.bin");
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _tree.Searcher().SearchAsync(
            new DuplicateSearch(MatchCriteria.Content, [Searched("Data")]), _tree.Policy(), ct: stop.Token));
    }

    /// <summary>
    /// What the content stage leaves out is counted through the search, each in its own count and
    /// added to what finding the candidates left out: a file held by another program and one whose
    /// data is refused are failed reads, one whose time moves before it is read has changed, one
    /// deleted has gone, and one Windows will no longer describe is unidentified, never gone. Each
    /// count that is not zero is a different number, the empty files finding left out included, so a
    /// file counted as another kind, or two counts swapped where the stages are added, changes two
    /// of them.
    /// </summary>
    [Fact]
    public async Task WhatTheContentStageLeavesOutIsCountedThroughTheSearch()
    {
        var content = Random(5000, 7);
        _tree.File(content, "Data", "a.bin");
        _tree.File(content, "Data", "b.bin");
        var locked = _tree.File(content, "Data", "locked.bin");
        var refused = _tree.File(content, "Data", "refused.bin");
        foreach (var i in Enumerable.Range(1, 4))
        {
            _tree.File(content, "Data", $"changes{i}.bin");
        }

        foreach (var i in Enumerable.Range(1, 3))
        {
            _tree.File(content, "Data", $"vanishes{i}.bin");
        }

        _tree.File(content, "Data", "Shut", "undescribed.bin");
        foreach (var i in Enumerable.Range(1, 5))
        {
            _tree.File(0, "Data", $"empty{i}.bin");
        }

        using var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var denied = DeniedDirectory.WithUnreadableContent(refused);
        DeniedDirectory? undescribed = null;

        try
        {
            var result = await _tree.Searcher((file, part, checksum, ct) =>
            {
                if (file.Name.StartsWith("changes", StringComparison.Ordinal))
                {
                    File.SetLastWriteTimeUtc(file.Path, file.Modified.AddSeconds(1));
                }
                else if (file.Name.StartsWith("vanishes", StringComparison.Ordinal))
                {
                    File.Delete(file.Path);
                }
                else if (file.Name == "undescribed.bin")
                {
                    undescribed = DeniedDirectory.WithUnreadableFile(file.Path);
                }

                return ContentReader.Default.Read(file, part, checksum, ct);
            }).SearchAsync(new DuplicateSearch(MatchCriteria.Content, [Searched("Data")]), _tree.Policy());

            Assert.Equal(["a.bin", "b.bin"], Assert.Single(result.Groups).Files.Select(file => file.Name).Order());
            Assert.Equal(2, result.LeftOut.ReadFailed);
            Assert.Equal(4, result.LeftOut.Changed);
            Assert.Equal(3, result.LeftOut.Gone);
            Assert.Equal(1, result.LeftOut.Unidentified);
            Assert.Equal(5, result.LeftOut.Empty);
            Assert.Equal(
                new LeftOutFiles(Links: 0, Empty: 5, UnknownLength: 0, OnlyInTheCloud: 0, Gone: 3, Unidentified: 1, ReadFailed: 2, Changed: 4),
                result.LeftOut);
        }
        finally
        {
            undescribed?.Dispose();
        }
    }

    /// <summary>Progress names each content stage it reaches, with the files it reads.</summary>
    [Fact]
    public async Task ProgressNamesEachStage()
    {
        var content = Random(3 * Block, 6);
        _tree.File(content, "Data", "a.bin");
        _tree.File(content, "Data", "b.bin");
        List<DuplicateSearchProgress> reports = [];

        await _tree.Searcher().SearchAsync(
            new DuplicateSearch(MatchCriteria.Content, [Searched("Data")]),
            _tree.Policy(),
            progress: new CallbackProgress<DuplicateSearchProgress>(report =>
            {
                lock (reports)
                {
                    reports.Add(report);
                }
            }));

        Assert.Contains(new DuplicateSearchProgress(DuplicateSearchStage.FirstAndLastBlocks, 2, 2), reports);
        Assert.Contains(new DuplicateSearchProgress(DuplicateSearchStage.FullContent, 2, 2), reports);
    }

    /// <summary>
    /// Runs a search whose reader stops it on read <paramref name="at"/>, counting the reads that
    /// start and the groups confirmed after the stop.
    /// </summary>
    private async Task<(DuplicateSearchResult Result, int ReadsAfterTheStop, int ConfirmedAfterTheStop)> StopAtRead(int at, MatchCriteria criteria)
    {
        using var stop = new CancellationTokenSource();
        var reads = 0;
        var readsAfterTheStop = 0;
        var confirmedAfterTheStop = 0;

        var result = await _tree.Searcher((file, part, checksum, ct) =>
        {
            if (stop.IsCancellationRequested)
            {
                Interlocked.Increment(ref readsAfterTheStop);
            }

            var reading = ContentReader.Default.Read(file, part, checksum, ct);

            if (Interlocked.Increment(ref reads) == at)
            {
                stop.Cancel();
            }

            return reading;
        }).SearchAsync(
            new DuplicateSearch(criteria, [Searched("Data")]),
            _tree.Policy(),
            new CallbackProgress<DuplicateGroup>(_ =>
            {
                if (stop.IsCancellationRequested)
                {
                    Interlocked.Increment(ref confirmedAfterTheStop);
                }
            }),
            ct: stop.Token);

        return (result, readsAfterTheStop, confirmedAfterTheStop);
    }
}
