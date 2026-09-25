using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// What directories about to be removed whole hold, looked at on the disk immediately before they go.
///
/// <para><b>For a removal that cannot do less.</b> Windows' own Disk Cleanup handler clears its
/// directories whole, and a folder whose parts only mean something together is removed whole or not
/// at all. Neither can step over an Outlook data file (§9), leave a file the guard on recently changed
/// files would keep, or leave a folder it could not look inside. So the plan's answers, made minutes
/// earlier, are asked again here of the disk, and any one of them stops the removal.</para>
///
/// <para><b>A folder that would not be listed stops it too.</b> A removal Deguffer performs walks
/// what it can and leaves the rest, so a folder it could not list is a folder it did not touch. Windows'
/// handler takes ownership and removes it all the same, and nothing here can say what was inside it:
/// an Outlook data file, a file changed an hour ago. §9 says never, by any route, so "could not look"
/// reads as "might", and the removal does not run.</para>
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

        // An explicit stack rather than recursion, because these are entire Windows installations.
        var pending = new Stack<string>(paths.Select(LongPath.Extended));

        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();

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
                    // Not followed through a link, for the reason the removal walk gives: what is on
                    // the far side belongs to wherever the link points.
                    if (!entry.IsReparsePoint)
                    {
                        pending.Push(entry.FullName);
                    }
                }
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
    public string? WhyNot(string how, MinimumAge keep) => this switch
    {
        { Stores.Count: > 0 } =>
            $"Nothing was removed: this holds an Outlook data file, at {MailStorePlan.Name(Stores)}. "
            + $"{how}, and Deguffer never removes one.",

        { Unlisted.Count: > 0 } =>
            $"Nothing was removed: Windows would not let Deguffer look inside {MailStorePlan.Name(Unlisted)}, "
            + $"so it cannot tell whether an Outlook data file is there. {how}.",

        { Recent: true } =>
            $"Nothing was removed: something inside changed in the last {keep.Describe()}, which this plan "
            + $"was asked to leave alone. {how}.",

        _ => null,
    };
}
