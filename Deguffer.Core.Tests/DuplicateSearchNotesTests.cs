using Deguffer.Core.Duplicates;
using Deguffer.Core.InstalledApps;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the page says a search did not look at (§7.4): a search that skipped a place must never read
/// as one that found nothing there, so every member of the finding that names a place or a count is
/// said, and nothing is said where there is nothing to say.
/// </summary>
public sealed class DuplicateSearchNotesTests
{
    private static readonly SearchLocation Share = new(@"\\server.test\share");
    private static readonly SearchLocation Backup = new(@"C:\Backup", LocationRole.Reference);
    private static readonly SearchLocation Archive = new(@"C:\Windows\Archive", LocationRole.Reference);

    [Fact]
    public void ASearchThatSkippedNothingSaysNothing() =>
        Assert.Empty(DuplicateSearchNotes.Of(Finding()));

    [Fact]
    public void EveryPlaceTheSearchDidNotLookAtIsNamed()
    {
        var notes = DuplicateSearchNotes.Of(Finding(
            unsearched: [new UnsearchedLocation(Share, "It is on another computer.")],
            passedOver: [new PassedOverPlace(@"C:\Windows", "Windows keeps it.")],
            unread: [new UnreadPlace(@"C:\Users\other", "Windows would not list it.")],
            read: [new ReadLocation(@"C:\", "It was walked."), new ReadLocation(@"D:\", RouteNote: null)],
            setAside: [new SetAsideInstallLocation("Tool", @"C:\Users\testuser", "it is the profile of an account on this computer.")],
            unreadPrograms: [UninstallScope.Machine64]));

        Assert.Equal(
        [
            @"\\server.test\share was not searched. It is on another computer.",
            @"Passed over C:\Windows. Windows keeps it.",
            @"Not read: C:\Users\other. Windows would not list it.",
            @"C:\: It was walked.",
            @"'Tool' says it is installed in C:\Users\testuser, which was not passed over: it is the profile of an account on this computer.",
            "Windows would not read the programs installed for 'All users', so the folders they are installed in were not known, and may have been searched.",
        ],
        notes);
    }

    /// <summary>
    /// A reference inside a place passed over was not searched, and it stops every rule, so it is
    /// named in its own right. One already named as a location not searched is not named twice.
    /// </summary>
    [Fact]
    public void AReferenceThatWentUnsearchedIsNamedOnce()
    {
        var unresolved = new UnsearchedLocation(Backup, "Windows would not open it.");
        var notes = DuplicateSearchNotes.Of(Finding(
            unsearched: [unresolved],
            unsearchedReferences: [unresolved, new UnsearchedLocation(Archive, "Windows keeps it.")]));

        Assert.Equal(
        [
            @"The reference C:\Backup was not searched. Windows would not open it.",
            @"The reference C:\Windows\Archive was not searched. Windows keeps it.",
        ],
        notes);
    }

    [Fact]
    public void EveryFileLeftOutIsCounted()
    {
        var notes = DuplicateSearchNotes.Of(Finding(leftOut: new LeftOutFiles(
            Links: 1, Empty: 2, UnknownLength: 3, OnlyInTheCloud: 4, Gone: 5, Unidentified: 6, ReadFailed: 7, Changed: 1_000)));

        Assert.Equal(
        [
            "1 link was not followed: a link is never a file to match.",
            "2 empty files were left out: empty files are never matched.",
            "3 files were left out because the scan could not tell how long they are.",
            "4 cloud files were left out because they are not on this device, and reading one would download it.",
            "5 files were gone by the time the search looked at them.",
            "6 files were left out because Windows would not describe them. They may still be there.",
            "7 files were left out because they could not be read: another program held them, or Windows refused. They may still be there.",
            $"{1_000:N0} files were left out because they changed while the search read them.",
        ],
        notes);
    }

    /// <summary>
    /// The end of a search adds the files its reading left out and whether it was stopped, after
    /// the notes the finding gave, so those keep their places on the page.
    /// </summary>
    [Fact]
    public void TheEndOfASearchAddsItsNotesAfterTheFindings()
    {
        var finding = Finding(passedOver: [new PassedOverPlace(@"C:\Windows", "Windows keeps it.")], leftOut: default(LeftOutFiles) with { Empty = 2 });
        var atFinding = DuplicateSearchNotes.Of(finding);
        var atEnd = DuplicateSearchNotes.Of(
            new DuplicateSearchResult(finding, [], finding.LeftOut + (default(LeftOutFiles) with { ReadFailed = 1 }), Stopped: true));

        Assert.Equal(atFinding, atEnd.Take(atFinding.Count));
        Assert.Equal(
            [
                "1 file was left out because they could not be read: another program held them, or Windows refused. They may still be there.",
                DuplicateSearchNotes.Stopped,
            ],
            atEnd.Skip(atFinding.Count));
    }

    private static CandidateFinding Finding(
        IReadOnlyList<UnsearchedLocation>? unsearched = null,
        IReadOnlyList<UnsearchedLocation>? unsearchedReferences = null,
        IReadOnlyList<PassedOverPlace>? passedOver = null,
        IReadOnlyList<UnreadPlace>? unread = null,
        IReadOnlyList<ReadLocation>? read = null,
        IReadOnlyList<SetAsideInstallLocation>? setAside = null,
        IReadOnlyList<UninstallScope>? unreadPrograms = null,
        LeftOutFiles leftOut = default) =>
        new([], unsearched ?? [], unsearchedReferences ?? [], passedOver ?? [], unread ?? [], read ?? [], setAside ?? [], [], unreadPrograms ?? [], leftOut);
}
