using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;

namespace Deguffer.App.Shell;

/// <summary>
/// How a folder the user picks becomes an approved source folder, the same way from every page that
/// offers to add one: Settings, and the Storage rows waiting for one.
///
/// <para>One sequence rather than one per page, because every step in it is a safety step. The pick
/// has to be a folder on a disk (<see cref="PickedFolder"/>), the volume holding it has to be read
/// fresh so a cloud mount is warned about (<see cref="SourceRootApproval.For"/>), and the folder is
/// stored with the cloud approval only by way of the warning that explains it
/// (<see cref="SourceRootApproval.Accepted"/>). A page that skipped one would widen what Deguffer
/// deletes in a way the other page does not.</para>
/// </summary>
/// <param name="roots">Where the approved folders are kept, and the one place that writes them.</param>
/// <param name="volumes">Asked what each picked folder is stored on.</param>
/// <param name="prompt">
/// Deferred rather than handed over built: the dialog needs the page's <c>XamlRoot</c>, which does
/// not exist while the page's view-model is being constructed.
/// </param>
public sealed class SourceFolderAddition(SourceRootService roots, IVolumeInventory volumes, Func<ISourceFolderPrompt> prompt)
{
    /// <summary>
    /// Ask the user for a folder, warn about it where Deguffer has to, and approve it if they say yes.
    ///
    /// <para>The volumes are read again before the warning is decided. The list is kept for the life
    /// of a planning pass, so a cloud client that mounted itself since the last one would be missing
    /// from it and the folder would be stored with no warning shown. One enumeration of the machine's
    /// drives per folder the user picks by hand is not a cost worth a stale answer.</para>
    ///
    /// <para>Re-approving a folder that is already approved is passed through rather than cut short
    /// here, because that is how a folder a plan refused comes to carry the cloud approval.</para>
    /// </summary>
    public async Task<SourceFolderAdded> AddAsync(CancellationToken ct)
    {
        var asking = prompt();

        if (await asking.PickAsync() is not { } picked)
        {
            return new(SourceFolderChoice.None);
        }

        if (PickedFolder.OnDisk(picked) is not { } onDisk)
        {
            return new(SourceFolderChoice.NotOnDisk);
        }

        await volumes.RefreshAsync(ct);

        var approval = SourceRootApproval.For(volumes, onDisk);

        if (approval.NeedsConfirming && !await asking.AcceptsAsync(approval, ct))
        {
            return new(SourceFolderChoice.Declined);
        }

        return new(SourceFolderChoice.Approved, roots.Add(approval.Accepted()));
    }
}

/// <summary>What became of one request for a source folder.</summary>
public enum SourceFolderChoice
{
    /// <summary>The user closed the picker without choosing.</summary>
    None,

    /// <summary>The user chose something the picker allows that is not a folder on a disk, such as a library.</summary>
    NotOnDisk,

    /// <summary>The user chose a folder and then declined the warning about it.</summary>
    Declined,

    /// <summary>The user chose a folder and approved it. Whether it was stored is the save's outcome.</summary>
    Approved,
}

/// <summary>What one request for a source folder came to.</summary>
/// <param name="Choice">What the user did.</param>
/// <param name="Saved">
/// Whether the approval reached disk, for <see cref="SourceFolderChoice.Approved"/>. Every other choice
/// writes nothing.
/// </param>
public readonly record struct SourceFolderAdded(SourceFolderChoice Choice, SaveOutcome Saved = SaveOutcome.NotWritten)
{
    /// <summary>
    /// Whether a folder was chosen at all, so a page can stop saying an earlier choice was not on a
    /// disk only when the user has made another.
    /// </summary>
    public bool WasChosen => Choice != SourceFolderChoice.None;
}
