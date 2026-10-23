using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// What directories about to be removed whole hold, looked at on the disk immediately before they go.
///
/// <para><b>For a removal that cannot do less.</b> A tool's own command, a Recycle Bin emptied by
/// Windows or removed whole by Deguffer, Windows' own Disk Cleanup handler, a folder whose parts only
/// mean something together, and Explore moving a folder to the Recycle Bin all take their subject
/// whole. None can step over an Outlook data file (§9), and some cannot leave a file the guard on
/// recently changed files would keep. So the plan's answers, made minutes earlier, are asked again
/// here of the disk, and any one of them stops the removal. A removal Deguffer performs file by file
/// needs no such look, because its own walk meets every store as it goes and steps over it.</para>
///
/// <para><b>A folder that would not be listed stops it too.</b> A removal Deguffer performs walks
/// what it can and leaves the rest, so a folder it could not list is a folder it did not touch. A
/// removal that goes whole does not ask: the shell moves a folder without listing it, and Windows'
/// handler takes ownership and removes it all the same. Nothing here can say what was inside such a
/// folder: an Outlook data file, a file changed an hour ago. §9 says never, by any route, so "could not
/// look" reads as "might", and the removal does not run. The measurement behind the plan skipped the
/// same folder (§5.3), which is why the plan could not have said so first.</para>
///
/// <para><b>So does a file standing where a folder was.</b> Windows lists nothing there, but it
/// refused nothing either, so the path is named apart from a folder it would not list: the removal
/// would take a file the preview never showed, and saying Windows would not let Deguffer look
/// blames a refusal that did not happen.</para>
///
/// <para><b>What it costs is a walk of the folder, and it is paid only where it has to be.</b> It
/// reads names alone, and it never opens a file. The folder asked about is listed as it stands, link or
/// not, because that is what the removal it stands in front of will act on. No folder below it is
/// followed through a link, for the reason the removal walk gives: what is on the far side belongs to
/// wherever the link points. A file is asked about whatever mark it carries, as the removal walk asks
/// it.</para>
/// </summary>
/// <param name="Stores">Every Outlook data file found, in display form.</param>
/// <param name="Recent">Whether anything inside changed within the guard, where one is on.</param>
/// <param name="Unlisted">Every folder Windows would not list, in display form.</param>
/// <param name="NotFolders">Every path listed as a folder where something else now stands, in display form.</param>
public sealed record WholeTreeLook(
    IReadOnlyList<string> Stores,
    bool Recent,
    IReadOnlyList<string> Unlisted,
    IReadOnlyList<string> NotFolders)
{
    /// <summary>
    /// Look inside every one of <paramref name="paths"/> now, off the calling thread: the trees can
    /// hold hundreds of thousands of entries, and the caller may be resuming on the UI thread.
    /// </summary>
    public static Task<WholeTreeLook> TakeAsync(
        IReadOnlyList<string> paths,
        MinimumAge keep,
        CancellationToken ct) =>
        WorkerThreads.Shared.RunAsync(() => Take(paths, keep, WindowsFileSystem.Default, ct), ct);

    /// <summary>The same look through <paramref name="fs"/>, which is how a test hands it a refusal.</summary>
    internal static WholeTreeLook Take(IReadOnlyList<string> paths, MinimumAge keep, IFileSystem fs, CancellationToken ct) =>
        Take(paths, keep, fs, WalkTuning.Default, ct);

    /// <summary>The same look with <paramref name="tuning"/>'s workers, which is how a test sets them.</summary>
    internal static WholeTreeLook Take(
        IReadOnlyList<string> paths,
        MinimumAge keep,
        IFileSystem fs,
        WalkTuning tuning,
        CancellationToken ct)
    {
        // Written by several workers at once, and put in one order at the end, so the answer is the
        // same however their work interleaved.
        var stores = new ConcurrentQueue<string>();
        var unlisted = new ConcurrentQueue<string>();
        var notFolders = new ConcurrentQueue<string>();
        var recent = 0;

        // On the walk every scan takes, which lists with several workers and never recurses: these are
        // entire Windows installations and the deepest trees on a developer's disk. A command's measured
        // paths can nest, and the walk lists a path inside another once, where the outer walk meets it.
        // One the outer walk never meets, behind a link or a folder Windows would not list, it walks on
        // its own, because the removal acts on that path as well. A file is named only where it is a
        // store, which is all of a name this asks.
        RootSetWalk.Visit<string>(
            [.. paths.Select(path => WalkRoot.Of(path, default))],
            tuning,
            FileNames.Stores,
            fs.ListEntries,
            (_, _, contents, _) =>
            {
                // Gone since the plan was made, or never there: nothing inside it can be lost. A refusal
                // part-way still looks at what was listed, since every finding stops the removal and the
                // folder is named as unlisted either way.
                if (contents.End.Refusal is ListingRefusal.NotADirectory)
                {
                    notFolders.Enqueue(LongPath.Display(contents.Directory));
                }
                else if (contents.Presence is PathPresence.Refused)
                {
                    unlisted.Enqueue(LongPath.Display(contents.Directory));
                }

                // The walk enters every directory listed here itself, and never a link: what is on a
                // link's far side belongs to wherever it points. A link is still asked about for its
                // own timestamps.
                foreach (var entry in contents.Entries)
                {
                    Notice(entry);
                }

                foreach (var link in contents.Links)
                {
                    Notice(link);
                }

                foreach (var marked in contents.ReparseFiles)
                {
                    Notice(marked);
                }

                // Nothing is carried down: what a directory holds is the same whichever path asked.
                return contents.Directory;
            },
            static () => { },
            TimeProvider.System,
            ct);

        // A directory inside two of the paths is handed over once for each, so the same name arrives
        // twice, and in another case where a path names a folder in a case other than the disk's: the
        // walk matches a nested path as spelled, so it walks that one again on its own. A run reports
        // how many stores it held back, so one file counts once. Two stores whose names differ only in
        // case inside a case-sensitive folder are then named as one, and the removal stops all the same.
        return new WholeTreeLook(
            InOneOrder(stores),
            Volatile.Read(ref recent) == 1,
            InOneOrder(unlisted),
            InOneOrder(notFolders));

        // Ordered before the duplicates go, so the spelling kept is the same however the work interleaved.
        static IReadOnlyList<string> InOneOrder(IEnumerable<string> paths) =>
            [.. paths.Order(StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.Ordinal).Distinct(StringComparer.OrdinalIgnoreCase)];

        void Notice(WalkEntry entry)
        {
            if (keep.IsOn && keep.Protects(entry.NewestFileTime))
            {
                Volatile.Write(ref recent, 1);
            }

            // Whatever mark it carries: a OneDrive placeholder and a deduplicated file carry the mark
            // Windows puts on a link, and every removal this look stands in front of would take one.
            if (entry.IsMailStore)
            {
                stores.Enqueue(LongPath.Display(entry.FullName));
            }
        }
    }

    /// <summary>
    /// Why the removal must not run, written for the user, or null where nothing found stops it. A
    /// store is named first, because it is the one reason no setting can change.
    /// </summary>
    /// <param name="lead">What happened instead, which opens the sentence: "Not run" for a command.</param>
    /// <param name="how">Why the removal cannot leave the part that stops it, as a clause.</param>
    public string? WhyNot(string lead, string how, MinimumAge keep) => this switch
    {
        { Stores.Count: > 0 } =>
            $"{lead}: this holds an Outlook data file, at {MailStorePlan.Name(Stores)}. "
            + $"{how}, and Deguffer never removes one.",

        { Unlisted.Count: > 0 } =>
            $"{lead}: Windows would not let Deguffer look inside {MailStorePlan.Name(Unlisted)}, "
            + $"so it cannot tell whether an Outlook data file is there. {how}.",

        { NotFolders.Count: > 0 } =>
            $"{lead}: a file now stands where the folder {MailStorePlan.Name(NotFolders)} was, "
            + $"so this is not what the preview showed. {how}.",

        { Recent: true } =>
            $"{lead}: something inside {keep.DescribeChange()}, which this plan "
            + $"was asked to leave alone. {how}.",

        _ => null,
    };
}
