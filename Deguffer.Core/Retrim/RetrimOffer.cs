using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Retrim;

/// <summary>
/// Which volumes a finished clean offers Windows' retrim on, and why.
///
/// <para>A clean frees clusters, and on a thin, virtual or solid-state volume the storage behind it
/// learns they are free only when the volume is trimmed. Windows trims on a schedule, so a virtual
/// machine's host or a thin pool can go on counting the space as used for days. The offer is made, and
/// never acted on, because the user asks for each retrim.</para>
///
/// <para>Decides and does no I/O of its own: the volumes come in as read, and their storage through a
/// function a <see cref="VolumeMediaCache"/> answers, asked only of a volume that freed enough.</para>
/// </summary>
public static class RetrimOffer
{
    /// <summary>
    /// The least a clean must free on one volume for a retrim to be offered there: 1 GiB. Below it the
    /// space Windows' own schedule leaves unclaimed for a few days is not worth a question.
    /// </summary>
    public const long Threshold = 1L << 30;

    /// <summary>
    /// Why a retrim is offered on <paramref name="volume"/>, or null where it is not.
    ///
    /// <para>Only a fixed volume that answers and that Windows names: a removable one is classed by its
    /// bridge, which does not say what is behind it, and a retrim is asked of a volume by its name.
    /// Thin comes first, because it is the case where space goes back to something else. A disk that
    /// would not say whether it is thin is offered nothing on that account.</para>
    /// </summary>
    public static RetrimBasis? BasisOf(LocalVolume volume, VolumeMedia media)
    {
        if (volume is not { Kind: DriveType.Fixed, Readiness: VolumeReadiness.Ready, VolumeName: not null })
        {
            return null;
        }

        if (media.ThinlyProvisioned is true)
        {
            return RetrimBasis.ThinlyProvisioned;
        }

        return media.Class switch
        {
            StorageMedia.Virtual => RetrimBasis.VirtualDisk,
            StorageMedia.Nvme or StorageMedia.SolidState => RetrimBasis.SolidState,
            _ => null,
        };
    }

    /// <summary>
    /// The volumes in <paramref name="after"/> whose free space grew by more than
    /// <see cref="Threshold"/> since <paramref name="before"/> and that a retrim is offered on, in the
    /// order <paramref name="after"/> lists them.
    ///
    /// <para>A volume is matched by the name Windows knows it by, so a letter moved to another volume
    /// during the clean is not compared with the one it left. A volume with no free figure on either
    /// side is passed over, as a growth from one reading alone would be a guess. The storage is asked
    /// only of a volume that freed enough, because each answer opens a device.</para>
    /// </summary>
    public static IReadOnlyList<RetrimCandidate> For(
        IReadOnlyList<LocalVolume> before,
        IReadOnlyList<LocalVolume> after,
        Func<LocalVolume, VolumeMedia> media)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(media);

        var freeBefore = before
            .Where(volume => volume is { VolumeName: not null, FreeBytes: not null })
            .GroupBy(volume => volume.VolumeName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().FreeBytes!.Value, StringComparer.OrdinalIgnoreCase);

        List<RetrimCandidate> offered = [];

        foreach (var volume in after)
        {
            if (volume is not { VolumeName: { } name, FreeBytes: { } now }
                || !freeBefore.TryGetValue(name, out var was)
                || now - was <= Threshold)
            {
                continue;
            }

            if (BasisOf(volume, media(volume)) is { } basis)
            {
                offered.Add(new RetrimCandidate(volume, now - was, basis));
            }
        }

        return offered;
    }
}
