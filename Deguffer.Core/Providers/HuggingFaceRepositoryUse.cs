using System.IO.Enumeration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether anything is using one repository in the Hugging Face hub cache: a program running or
/// working inside its folder, or holding one of its files open.
///
/// <para><b>No signal of the tool's own, so the files themselves.</b> <c>huggingface_hub</c> takes
/// a lock only while it downloads, and a library that has loaded a model keeps no lock at all. What
/// it keeps is the weights file, open or mapped for as long as the model is loaded. Each file a
/// revision lists is asked about, through its entry in <c>snapshots</c>: that is the path a library
/// opens, and the Restart Manager follows it to the blob it links to, in the repository's own
/// <c>blobs</c> or in the shared store.</para>
///
/// <para><b>Asked again at the clean.</b> A model can be loaded while the preview is on screen, and
/// Windows refuses to delete a file a program has open, so <c>hf cache rm</c> would stop part-way
/// and leave a revision whose links point at files that are gone. A plan offers the repository only
/// on a whole answer that nothing is using it, and the run asks again immediately before the command,
/// about the files there are then.</para>
/// </summary>
/// <param name="Inspector">What reads the process table and asks the Restart Manager.</param>
/// <param name="Folder">The repository's folder, in display form.</param>
internal sealed record HuggingFaceRepositoryUse(ILiveTreeInspector Inspector, string Folder) : ICommandTargetCheck
{
    /// <summary>Why a repository a program is using survives a clean.</summary>
    public const string InUseReason = "A program is using this repository, so it is left alone.";

    /// <summary>
    /// The question about <paramref name="folder"/>, built from its files as they are now, or null where
    /// Windows would not list them all: a file nobody listed is a file nobody asked about.
    /// </summary>
    public static LiveTreeQuery? Query(string folder) =>
        SnapshotFiles(folder) is { } files ? new LiveTreeQuery(folder, folder, files) : null;

    /// <summary>
    /// The repositories nothing is found using, on a whole answer, from one reading of the machine. One
    /// a program is using, or one the question about could not be asked in full, is held back with a
    /// note, and named for §5.6.
    /// </summary>
    /// <param name="inspector">What reads the process table and asks the Restart Manager.</param>
    /// <param name="idsByFolder">Each repository's folder, in display form, and the id it is named to the user by.</param>
    public static HuggingFaceUse Partition(ILiveTreeInspector inspector, IReadOnlyDictionary<string, string> idsByFolder)
    {
        var queries = idsByFolder.Keys.Select(folder => (Folder: folder, Query: Query(folder))).ToList();
        var asked = queries.Select(item => item.Query).OfType<LiveTreeQuery>().ToList();
        var findings = asked.Count > 0 ? inspector.FindLive(asked) : LiveTreeFindings.Nothing;

        var unused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var held = new List<(string Path, string Reason)>();
        var cannotTell = 0;

        foreach (var (folder, query) in queries)
        {
            if (query is not null && findings.IsLive(folder))
            {
                held.Add((folder, InUseReason));
            }
            else if (query is not null && findings.IsAnswered(folder))
            {
                unused.Add(folder);
            }
            else
            {
                held.Add((folder, LiveTreeVeto.UncheckedReason));
                cannotTell++;
            }
        }

        var notes = new List<PlanNote>();

        if (LiveTreeVeto.NoteFor([.. findings.Live.Where(live => idsByFolder.ContainsKey(live.Directory))], live => idsByFolder[live.Directory]) is { } inUse)
        {
            notes.Add(inUse);
        }

        if (LiveTreeVeto.UncheckedNote(cannotTell) is { } incomplete)
        {
            notes.Add(incomplete);
        }

        return new HuggingFaceUse(unused, held, notes);
    }

    public string? WhyNot(RunCommandStep step, CancellationToken ct)
    {
        if (Query(Folder) is not { } query)
        {
            return "Windows would not list every file in the repository, so Deguffer cannot tell whether a program has one open";
        }

        var findings = Inspector.FindLive([query], ct);

        if (findings.Live.FirstOrDefault(live => live.Directory.Equals(Folder, StringComparison.OrdinalIgnoreCase)) is { } used)
        {
            return $"{string.Join("; ", used.Holders)}, so the repository is in use";
        }

        return findings.IsAnswered(Folder)
            ? null
            : "Deguffer could not tell whether a program has the repository's files open";
    }

    /// <summary>
    /// Every file in every revision of the repository, links included, which is what a library opens.
    /// Nothing under <c>snapshots</c> is followed through a link to a folder, because a revision is
    /// files and folders the tool made, and a linked folder is somewhere else.
    /// </summary>
    private static IReadOnlyList<string>? SnapshotFiles(string folder)
    {
        var snapshots = Path.Combine(folder, "snapshots");

        // A revision folder Windows will not describe may hold the file a program has open.
        switch (LongPath.ProbeDirectory(snapshots))
        {
            case PathPresence.Absent:
                return [];

            case PathPresence.Refused:
                return null;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.None,
            IgnoreInaccessible = false,
        };

        try
        {
            return
            [
                .. new FileSystemEnumerable<string>(
                    LongPath.Extended(snapshots),
                    (ref FileSystemEntry entry) => LongPath.Display(entry.ToFullPath()),
                    options)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
                    ShouldRecursePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0,
                },
            ];
        }

        // A folder Windows refused, or one removed while it was listed: either way the list is not whole.
        catch (Exception refused) when (refused is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>What one reading of the machine found about the repositories a plan considered.</summary>
/// <param name="Unused">The folders nothing was found using, on a whole answer.</param>
/// <param name="Held">The folders held back, with why, for §5.6.</param>
/// <param name="Notes">What the user is told about the folders held back.</param>
internal sealed record HuggingFaceUse(
    IReadOnlySet<string> Unused,
    IReadOnlyList<(string Path, string Reason)> Held,
    IReadOnlyList<PlanNote> Notes);
