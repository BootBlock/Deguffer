using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Retrim;

/// <summary>
/// Asks Windows to retrim the volumes a request names, one at a time, judging each again first.
///
/// <para>A request can come from the command line of a restart as administrator, so its volumes are
/// names in text, not a decision. Each is found among the volumes now mounted by the name Windows
/// knows it by, never by its mount point, which a letter moved to another volume would make name a
/// volume the user did not accept. It is judged by <see cref="RetrimOffer.BasisOf"/> again, and Windows
/// is asked to retrim only one that still qualifies.
/// The freed figure is not judged again: it described the clean the user was looking at when they
/// asked.</para>
///
/// <para>One at a time, because two retrims of volumes on one disk compete for it.</para>
/// </summary>
public static class RetrimRun
{
    /// <summary>
    /// Retrim each of <paramref name="targets"/> in turn, reporting each result to
    /// <paramref name="landed"/> as Windows gives it.
    ///
    /// <para>A cancellation stops the run before the next volume, and throws. Windows' own call cannot
    /// be stopped once made, so a volume being retrimmed when the token is cancelled is waited for and
    /// reported first.</para>
    /// </summary>
    /// <param name="volumes">The volumes mounted now.</param>
    /// <param name="media">The storage behind a volume. Blocks on its device, so it is asked off the calling thread.</param>
    public static async Task ExecuteAsync(
        IReadOnlyList<RetrimTarget> targets,
        IReadOnlyList<LocalVolume> volumes,
        Func<LocalVolume, VolumeMedia> media,
        IVolumeRetrim retrim,
        IProgress<RetrimResult> landed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(retrim);
        ArgumentNullException.ThrowIfNull(landed);

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            // Not cancelled by the token once started: the answer of a retrim Windows has begun is
            // the one thing the user must see.
            landed.Report(await Task.Run(() => One(target, volumes, media, retrim), CancellationToken.None));
        }
    }

    private static RetrimResult One(RetrimTarget target, IReadOnlyList<LocalVolume> volumes, Func<LocalVolume, VolumeMedia> media, IVolumeRetrim retrim)
    {
        foreach (var volume in volumes)
        {
            if (string.Equals(volume.VolumeName, target.VolumeName, StringComparison.OrdinalIgnoreCase))
            {
                return RetrimOffer.BasisOf(volume, media(volume)) is not null
                    ? new RetrimResult(target.Root, retrim.Retrim(target.VolumeName))
                    : new RetrimResult(target.Root, new RetrimAnswer(RetrimOutcome.NotOffered));
            }
        }

        return new RetrimResult(target.Root, new RetrimAnswer(RetrimOutcome.NotOffered));
    }
}
