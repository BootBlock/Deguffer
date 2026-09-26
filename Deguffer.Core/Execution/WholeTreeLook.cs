using Deguffer.Core.Safety;

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
public sealed record WholeTreeLook(IReadOnlyList<string> Stores, bool Recent, IReadOnlyList<string> Unlisted)
{
    /// <summary>
    /// Look inside every one of <paramref name="paths"/> now, off the calling thread: the trees can
    /// hold hundreds of thousands of entries, and the caller may be resuming on the UI thread.
    /// </summary>
    public static Task<WholeTreeLook> TakeAsync(
        IReadOnlyList<string> paths,
        MinimumAge keep,
        CancellationToken ct) =>
        Task.Run(() => Take(paths, keep, WindowsFileSystem.Default, ct), ct);

    /// <summary>The same look through <paramref name="fs"/>, which is how a test hands it a refusal.</summary>
    internal static WholeTreeLook Take(IReadOnlyList<string> paths, MinimumAge keep, IFileSystem fs, CancellationToken ct)
    {
        var stores = new List<string>();
        var unlisted = new List<string>();
        var recent = false;

        // A command's measured paths can nest, and a folder walked twice would name its stores twice.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // An explicit stack rather than recursion, because these are entire Windows installations and
        // the deepest trees on a developer's disk.
        var pending = new Stack<string>(paths.Select(LongPath.Extended));

        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();

            if (!visited.Add(directory))
            {
                continue;
            }

            IReadOnlyList<FileSystemEntry> entries;
            try
            {
                entries = fs.EnumerateEntries(directory);
            }
            catch (DirectoryNotFoundException)
            {
                // Gone since the plan was made, or never there: nothing inside it can be lost.
                continue;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                unlisted.Add(LongPath.Display(directory));
                continue;
            }

            foreach (var entry in entries)
            {
                recent |= keep.IsOn && keep.Protects(entry.NewestFileTime);

                if (entry.IsDirectory)
                {
                    if (!entry.IsReparsePoint)
                    {
                        pending.Push(entry.FullName);
                    }
                }

                // Whatever mark it carries: a OneDrive placeholder and a deduplicated file carry the mark
                // Windows puts on a link, and every removal this look stands in front of would take one.
                else if (MailStore.Is(entry.FullName))
                {
                    stores.Add(LongPath.Display(entry.FullName));
                }
            }
        }

        return new WholeTreeLook(stores, recent, unlisted);
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

        { Recent: true } =>
            $"{lead}: something inside {keep.DescribeChange()}, which this plan "
            + $"was asked to leave alone. {how}.",

        _ => null,
    };
}
