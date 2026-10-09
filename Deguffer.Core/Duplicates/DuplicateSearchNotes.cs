using Deguffer.Core.InstalledApps;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// What the page says a search did not look at (§7.4): each location not searched, each place passed
/// over or not read, each install location set aside, and every file left out, counted. A search
/// that skipped a place must never read as one that found nothing there.
///
/// <para>One list of sentences in a fixed order, so the page keeps no rule about which of the
/// finding's members to show, and each sentence opens with what it is about, so the order groups
/// them: the locations not searched first, because each is one the user asked for, and what only
/// the end of a search can say (the files left out as they were read, and a stop) last, so the notes
/// shown once the candidates are found keep their places when it ends.</para>
/// </summary>
public static class DuplicateSearchNotes
{
    /// <summary>The note on a search that was stopped, which comes last.</summary>
    public const string Stopped =
        "The search was stopped before it had read every file, so files may match that are in no group.";

    /// <summary>The notes for what finding the candidates found, before any content is read.</summary>
    public static IReadOnlyList<string> Of(CandidateFinding finding) => Of(finding, finding.LeftOut, stopped: false);

    /// <summary>The notes for a search that has ended.</summary>
    public static IReadOnlyList<string> Of(DuplicateSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return Of(result.Finding, result.LeftOut, result.Stopped);
    }

    private static List<string> Of(CandidateFinding finding, LeftOutFiles leftOut, bool stopped)
    {
        ArgumentNullException.ThrowIfNull(finding);

        List<string> notes = [];

        foreach (var location in finding.Unsearched)
        {
            notes.Add($"{Located(location.Given)} was not searched. {location.Reason}");
        }

        // A reference passed over or not read is not among the locations not searched, and it stops
        // every rule, so it is named in its own right rather than only as the place that held it.
        foreach (var reference in finding.UnsearchedReferences.Where(reference =>
            !finding.Unsearched.Any(unsearched => ReferenceEquals(unsearched.Given, reference.Given))))
        {
            notes.Add($"{Located(reference.Given)} was not searched. {reference.Reason}");
        }

        foreach (var place in finding.PassedOver)
        {
            notes.Add($"Passed over {place.Path}. {place.Reason}");
        }

        foreach (var place in finding.Unread)
        {
            notes.Add($"Not read: {place.Path}. {place.Reason}");
        }

        foreach (var read in finding.Read.Where(read => read.RouteNote is not null))
        {
            notes.Add($"{read.Folder}: {read.RouteNote}");
        }

        foreach (var setAside in finding.SetAside)
        {
            notes.Add($"'{setAside.Program}' says it is installed in {setAside.Location}, which was not passed over: {setAside.Reason}");
        }

        foreach (var scope in finding.UnreadProgramLists)
        {
            notes.Add($"Windows would not read the programs installed for '{scope.Describe()}', so the folders they are "
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
            notes.Add(Stopped);
        }

        return notes;

        void Counted(int count, string one, string many, string what)
        {
            if (count > 0)
            {
                notes.Add($"{count:N0} {(count == 1 ? one : many)} {what}");
            }
        }
    }

    private static string Located(SearchLocation location) =>
        location.Role == LocationRole.Reference ? $"The reference {location.Path}" : location.Path;
}
