using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// What an Explore item holds, looked at immediately before the shell is asked to move it to the
/// Recycle Bin.
///
/// <para><b>§9 one level up.</b> A folder is not a store, so the policy allows it, and the shell moves
/// a folder whole: any store inside it would go along, one emptied bin from gone. The policy is asked
/// about paths and cannot look inside, so the look is taken here, and a refusal names the store. A link
/// is moved as a link and takes nothing on its far side, so nothing is looked for through one.</para>
///
/// <para>It is not said before the confirmation, and that is a stated boundary rather than an
/// oversight: knowing it beforehand means a walk of the folder on every change of selection, and this
/// look is the one that decides.</para>
///
/// <para><b>A folder Windows will not describe is left where it is.</b> The link question fails closed,
/// so asked first it would read the refusal as "a link" and skip the look, and a folder nobody could
/// classify would go to the shell unexamined. The permanent route's <see cref="DirectoryRemover"/>
/// gives a root it cannot describe the same answer, and leaves it standing.</para>
///
/// <para><b>A folder Windows describes but will not list, here or anywhere below, is left where it is
/// too.</b> The shell moves it without listing it, so "could not look" reads as "might hold one". See
/// <see cref="WholeTreeLook"/>.</para>
/// </summary>
internal static class RecycleBinLook
{
    /// <summary>Why <paramref name="item"/> must stay out of the Recycle Bin, or null where it may go.</summary>
    public static async Task<string?> WhyItStaysAsync(ExploreItem item, IFileSystem fs, CancellationToken ct)
    {
        if (!item.IsDirectory)
        {
            return null;
        }

        var name = Path.GetFileName(item.Path);

        if (fs.ProbeDirectory(LongPath.Extended(item.Path)) is PathPresence.Refused)
        {
            return $"Windows would not say what is in '{name}', so Deguffer could not check it for an Outlook "
                + "data file. Deguffer never removes one, so it left the folder alone.";
        }

        if (fs.IsReparsePoint(LongPath.Extended(item.Path)))
        {
            return null;
        }

        var look = await WorkerThreads.Shared.RunAsync(
            () => WholeTreeLook.Take([item.Path], MinimumAge.Off, fs, ct), ct).ConfigureAwait(false);

        // The probe above reads a file where the folder was as no folder, never a refusal, so the look
        // meets it, and names it apart from a folder Windows would not list.
        return look switch
        {
            { Stores.Count: > 0 } =>
                $"'{name}' holds an Outlook data file, at {MailStorePlan.Name(look.Stores)}. "
                + "Moving the folder to the Recycle Bin would take it along, and Deguffer never removes one.",

            { Unlisted.Count: > 0 } =>
                $"Windows would not let Deguffer look inside {MailStorePlan.Name(look.Unlisted)}, so Deguffer "
                + $"could not check '{name}' for an Outlook data file. Deguffer never removes one, so it left "
                + "the folder alone.",

            { NotFolders.Count: > 0 } =>
                $"Not moved to the Recycle Bin: a file now stands where the folder {MailStorePlan.Name(look.NotFolders)} "
                + "was, and Deguffer left it alone.",

            _ => null,
        };
    }
}
