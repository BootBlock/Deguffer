using Deguffer.Core.Duplicates;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The scene §7.4's removal tests share: real files on the scratch drive of
/// <see cref="DuplicateMarkingScene"/>, each a copy as a search identifies it, marked as a user
/// would, and removed by the remover with only the Recycle Bin and the last step of a permanent
/// removal stood in for, so a test can act at the last moment before a copy goes.
/// </summary>
public abstract class DuplicateRemovalScene : DuplicateMarkingScene
{
    /// <summary>Where the stand-in Recycle Bin puts what it takes: a folder of the scratch drive, so a move keeps the file ID.</summary>
    private protected string Bin => Path.Combine(_tree.Top, "bin");

    /// <summary>A file holding <paramref name="content"/> at <paramref name="path"/>, its folder created.</summary>
    private protected static string Write(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);

        return path;
    }

    /// <summary><paramref name="length"/> bytes that differ from any other call's, so two copies match only where a test makes them.</summary>
    private protected static byte[] Content(int length = 3 * 64 * 1024, int seed = 297)
    {
        var content = new byte[length];
        new Random(seed).NextBytes(content);

        return content;
    }

    /// <summary>The file at <paramref name="path"/> as a search identifies it, on the scene's internal drive.</summary>
    private protected DuplicateCandidate Found(string path, LocationRole role = LocationRole.Search)
    {
        var description = FileInformation.Default.Describe(path, IdentityRoute.FileId).Description!;

        return new DuplicateCandidate(
            description.Identity,
            description.Path,
            Path.GetFileName(description.Path),
            [description.Path],
            description.Names,
            description.Length,
            description.Allocated,
            description.Modified,
            StorageAttributes.Of(description.Attributes),
            role)
        {
            Route = IdentityRoute.FileId,
            Volume = _internal,
            Attributes = description.Attributes,
        };
    }

    /// <summary>The marks for groups whose checksum, where one is given, every file shares.</summary>
    private protected DuplicateMarks MarksWithChecksum(ContentChecksum? checksum, params DuplicateCandidate[][] groups) =>
        MarksOf(new DuplicateSearchResult(
            new CandidateFinding(
                [.. groups.Select(files => new CandidateGroup(files[0].Length, Name: null, Modified: null, files))],
                [],
                _unsearchedReferences,
                [],
                [],
                [],
                [],
                _programs,
                [],
                default),
            [.. groups.Select(files => new DuplicateGroup(MatchCriteria.Content, files[0].Length, checksum, files))],
            default,
            Stopped: false));

    /// <summary>Mark each of <paramref name="copies"/>, which the marks must allow.</summary>
    private protected static void Mark(DuplicateMarks marks, params DuplicateCandidate[] copies)
    {
        foreach (var copy in copies)
        {
            var group = marks.Groups.Single(group => group.Group.Files.Contains(copy));
            Assert.Null(group.Mark(copy, marks.Keeping));
        }
    }

    /// <summary>
    /// Confirm the marks that stand and remove them, as the page does, with the keeping rule as the
    /// marks last judged it.
    /// </summary>
    /// <param name="bin">The Recycle Bin, which moves what it takes into <see cref="Bin"/> unless a test says otherwise.</param>
    /// <param name="delete">The last step of a permanent removal, which deletes through the compared handle unless a test says otherwise.</param>
    private protected DuplicateRemovalReport Remove(
        DuplicateMarks marks,
        ExploreRemovalMode mode,
        IRecycleBin? bin = null,
        HandleDeleter? delete = null,
        CopyOpener? open = null,
        FileInformation? files = null)
    {
        var remover = Remover(bin, delete, open, files);
        var confirmation = RemovalConfirmation.For(marks, marks.Keeping, mode, _ => null, remover.WhyTheBinCannotTake);
        var (plan, dropped) = DuplicateRemover.Plan(marks.Groups, confirmation.Copies, marks.Keeping);

        return remover.Remove(plan, dropped, marks.Keeping, confirmation.Mode, CancellationToken.None);
    }

    private protected DuplicateRemover Remover(
        IRecycleBin? bin = null, HandleDeleter? delete = null, CopyOpener? open = null, FileInformation? files = null) =>
        new(
            files ?? FileInformation.Default,
            open ?? FileInformation.OpenHeld,
            delete ?? HandleDeletion.Delete,
            bin ?? FakeRecycleBin.MovingTo(Bin),
            WindowsFileSystem.Default);
}
