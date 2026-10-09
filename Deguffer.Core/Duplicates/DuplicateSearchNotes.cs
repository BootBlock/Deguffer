using Deguffer.Core.InstalledApps;

namespace Deguffer.Core.Duplicates;

/// <summary>What a <see cref="SearchNote"/> is about, so the page can group and mark the notes.</summary>
public enum SearchNoteKind
{
    /// <summary>A location the user chose that was not searched.</summary>
    NotSearched,

    /// <summary>A place inside a location that the search passed over by default.</summary>
    PassedOver,

    /// <summary>A place inside a location that Windows would not let the search read, in whole or in part.</summary>
    NotRead,

    /// <summary>What to know about the route a location was read by.</summary>
    Route,

    /// <summary>An install location that named nothing the search could pass over, or a list of programs Windows would not read.</summary>
    Programs,

    /// <summary>Files the search left out, counted.</summary>
    LeftOut,

    /// <summary>The search was stopped before it read every file.</summary>
    Stopped,
}

/// <summary>One sentence about what a duplicate search did not look at, or left out.</summary>
public sealed record SearchNote(SearchNoteKind Kind, string Text);

/// <summary>
/// What the page says a search did not look at (§7.4): each location not searched, each place passed
/// over or not read, each install location set aside, and every file left out, counted. A search
/// that skipped a place must never read as one that found nothing there.
///
/// <para>One list in a fixed order, so the page keeps no rule about which of the finding's members
/// to show: the locations not searched first, because each is one the user asked for, and what only
/// the end of a search can say (the files left out as they were read, and a stop) last, so the notes
/// shown once the candidates are found keep their places when it ends.</para>
/// </summary>
public static class DuplicateSearchNotes
{
    /// <summary>The notes for what finding the candidates found, before any content is read.</summary>
    public static IReadOnlyList<SearchNote> Of(CandidateFinding finding) => Of(finding, finding.LeftOut, stopped: false);

    /// <summary>The notes for a search that has ended.</summary>
    public static IReadOnlyList<SearchNote> Of(DuplicateSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return Of(result.Finding, result.LeftOut, result.Stopped);
    }

    private static List<SearchNote> Of(CandidateFinding finding, LeftOutFiles leftOut, bool stopped)
    {
        ArgumentNullException.ThrowIfNull(finding);

        List<SearchNote> notes = [];

        void Add(SearchNoteKind kind, string text) => notes.Add(new SearchNote(kind, text));

        foreach (var location in finding.Unsearched)
        {
            Add(SearchNoteKind.NotSearched, $"{Located(location.Given)} was not searched. {location.Reason}");
        }

        // A reference passed over or not read is not among the locations not searched, and it stops
        // every rule, so it is named in its own right rather than only as the place that held it.
        foreach (var reference in finding.UnsearchedReferences.Where(reference =>
            !finding.Unsearched.Any(unsearched => ReferenceEquals(unsearched.Given, reference.Given))))
        {
            Add(SearchNoteKind.NotSearched, $"{Located(reference.Given)} was not searched. {reference.Reason}");
        }

        foreach (var place in finding.PassedOver)
        {
            Add(SearchNoteKind.PassedOver, $"Passed over {place.Path}. {place.Reason}");
        }

        foreach (var place in finding.Unread)
        {
            Add(SearchNoteKind.NotRead, $"Not read: {place.Path}. {place.Reason}");
        }

        foreach (var read in finding.Read.Where(read => read.RouteNote is not null))
        {
            Add(SearchNoteKind.Route, $"{read.Folder}: {read.RouteNote}");
        }

        foreach (var setAside in finding.SetAside)
        {
            Add(SearchNoteKind.Programs,
                $"'{setAside.Program}' says it is installed in {setAside.Location}, which was not passed over: {setAside.Reason}");
        }

        foreach (var scope in finding.UnreadProgramLists)
        {
            Add(SearchNoteKind.Programs,
                $"Windows would not read the programs installed for '{scope.Describe()}', so the folders they are "
                + "installed in were not known, and may have been searched.");
        }

        Counted(leftOut.Links, "link was", "links were", "not followed: a link is never a file to match.");
        Counted(leftOut.Empty, "empty file was", "empty files were", "left out: empty files are never matched.");
        Counted(leftOut.UnknownLength, "file was", "files were", "left out because the scan could not tell how long they are.");
        Counted(leftOut.OnlyInTheCloud, "cloud file was", "cloud files were",
            "left out because they are not on this device, and reading one would download it.");
        Counted(leftOut.Gone, "file was", "files were", "gone by the time the search looked at them.");
        Counted(leftOut.Unidentified, "file was", "files were",
            "left out because Windows would not describe them. They may still be there.");
        Counted(leftOut.ReadFailed, "file was", "files were",
            "left out because they could not be read: another program held them, or Windows refused. They may still be there.");
        Counted(leftOut.Changed, "file was", "files were", "left out because they changed while the search read them.");

        if (stopped)
        {
            Add(SearchNoteKind.Stopped,
                "The search was stopped before it had read every file, so files may match that are in no group.");
        }

        return notes;

        void Counted(int count, string one, string many, string what)
        {
            if (count > 0)
            {
                Add(SearchNoteKind.LeftOut, $"{count:N0} {(count == 1 ? one : many)} {what}");
            }
        }
    }

    private static string Located(SearchLocation location) =>
        location.Role == LocationRole.Reference ? $"The reference {location.Path}" : location.Path;
}
