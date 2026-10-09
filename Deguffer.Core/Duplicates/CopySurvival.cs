using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// §5.6's question for a duplicate removal (§7.4): every copy a group kept, and every reference copy,
/// is still there afterwards with the same file ID, length and last-modified time it had as the
/// removal began.
///
/// <para><b>Known by its file ID, never by its path.</b> Each copy is described before the removal
/// and again after it through a handle opened by its number, so a copy is found wherever it is, and a
/// path compared without regard to case can never let a removed <c>a.txt</c> stand in for a lost
/// <c>A.txt</c>. Where a volume will not open a file by its number, the path is described instead
/// only where no link can be on it (<see cref="DuplicateCandidate.NoLinkOnItsPath"/>), and must
/// name the same identity.</para>
///
/// <para><b>Only what was there is asserted.</b> A copy gone before the removal began was not this
/// removal's to keep, and one Windows would not describe then or afterwards is a check that could
/// not be made, never one that passed.</para>
/// </summary>
internal sealed class CopySurvival
{
    private readonly FileInformation _files;
    private readonly List<(DuplicateCandidate Copy, FileReading Before, bool ById)> _before;

    private CopySurvival(FileInformation files, List<(DuplicateCandidate, FileReading, bool)> before)
    {
        _files = files;
        _before = before;
    }

    /// <summary>Describe every copy in <paramref name="copies"/>, immediately before the removal.</summary>
    public static CopySurvival Take(IEnumerable<DuplicateCandidate> copies, FileInformation files)
    {
        List<(DuplicateCandidate, FileReading, bool)> before = [];

        foreach (var copy in copies)
        {
            var byId = ById(files, copy);

            if (byId.Result is FileReadingResult.Identified)
            {
                before.Add((copy, byId, true));
            }
            else
            {
                before.Add((copy, copy.NoLinkOnItsPath ? ByPath(files, copy) : FileReading.Unreadable, false));
            }
        }

        return new CopySurvival(files, before);
    }

    /// <summary>One check for each copy that was there, unless <paramref name="removed"/> holds it.</summary>
    public IReadOnlyList<VerificationCheck> Verify(IReadOnlySet<FileIdentity> removed)
    {
        List<VerificationCheck> checks = [];

        foreach (var (copy, before, byId) in _before)
        {
            if (removed.Contains(copy.Identity) || before.Result is FileReadingResult.Gone)
            {
                continue;
            }

            var reason = copy.Role == LocationRole.Reference
                ? "A reference copy must be untouched."
                : "A copy the group kept must survive unchanged.";

            if (before.Description is not { } then)
            {
                checks.Add(new VerificationCheck(
                    copy.Path, reason, VerificationOutcome.Unverified,
                    "NOT CHECKED — Windows would not describe it by its file ID before the removal, so nothing shows whether it survived."));
                continue;
            }

            var after = byId ? ById(_files, copy) : ByPath(_files, copy);

            checks.Add(after switch
            {
                { Description: { } now } when now.Length == then.Length && now.Modified == then.Modified =>
                    new VerificationCheck(copy.Path, reason, VerificationOutcome.Survived, "Still present, unchanged."),
                { Description: not null } =>
                    new VerificationCheck(
                        copy.Path, reason, VerificationOutcome.Failed,
                        "CHANGED — it is still there, with another length or last-modified time than it had before the removal."),
                { Result: FileReadingResult.Gone } =>
                    new VerificationCheck(
                        copy.Path, reason, VerificationOutcome.Failed,
                        "MISSING — no file on its drive has its file ID any more."),
                _ => new VerificationCheck(
                    copy.Path, reason, VerificationOutcome.Unverified,
                    "NOT CHECKED — it was there before the removal, and Windows would not describe it afterwards, "
                    + "so nothing shows whether it survived."),
            });
        }

        return checks;
    }

    /// <summary>
    /// The copy described by its number on its volume. A file of that number with another volume's
    /// serial says the volume opened is not the copy's, which shows nothing about the copy.
    /// </summary>
    private static FileReading ById(FileInformation files, DuplicateCandidate copy) =>
        files.DescribeById(copy.Volume.RootPath, copy.Identity, copy.Route) switch
        {
            { Description: { } now } when now.Identity != copy.Identity => FileReading.Unreadable,
            var reading => reading,
        };

    /// <summary>
    /// The copy described by its path, read as gone where its path now names another file, because
    /// the copy is known by its identity.
    /// </summary>
    private static FileReading ByPath(FileInformation files, DuplicateCandidate copy) =>
        files.Describe(copy.Path, copy.Route) switch
        {
            { Description: { } now } when now.Identity != copy.Identity => FileReading.Gone,
            var reading => reading,
        };
}
