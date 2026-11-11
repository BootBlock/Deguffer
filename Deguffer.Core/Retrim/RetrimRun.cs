using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Retrim;

/// <summary>
/// Asks Windows to retrim the volumes a request names, one at a time, judging each again first.
///
/// <para>A request can come from the command line of a restart as administrator, so its volumes are
/// names in text, not a decision. Each is found among the volumes now mounted and judged by
/// <see cref="RetrimOffer.BasisOf"/> again, and Windows is asked to retrim only one that still qualifies.
/// The freed figure is not judged again: it described the clean the user was looking at when they
/// asked.</para>
///
/// <para>One at a time, because two retrims of volumes on one disk compete for it, and Windows' own
/// scheduled optimisation runs volumes one after another too.</para>
/// </summary>
public static class RetrimRun
{
    /// <summary>
    /// Retrim each of <paramref name="roots"/> in turn, reporting each result to
    /// <paramref name="landed"/> as Windows gives it.
    ///
    /// <para>A cancellation stops the run before the next volume, and throws. Windows' own call cannot
    /// be stopped once made, so a volume being retrimmed when the token is cancelled is waited for and
    /// reported first.</para>
    /// </summary>
    /// <param name="volumes">The volumes mounted now.</param>
    /// <param name="media">The storage behind a volume. Blocks on its device, so it is asked off the calling thread.</param>
    public static async Task ExecuteAsync(
        IReadOnlyList<string> roots,
        IReadOnlyList<LocalVolume> volumes,
        Func<LocalVolume, VolumeMedia> media,
        IVolumeRetrim retrim,
        IProgress<RetrimResult> landed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(retrim);
        ArgumentNullException.ThrowIfNull(landed);

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();

            // Not cancelled by the token once started: the answer of a retrim Windows has begun is
            // the one thing the user must see.
            landed.Report(await Task.Run(() => One(root, volumes, media, retrim), CancellationToken.None));
        }
    }

    private static RetrimResult One(string root, IReadOnlyList<LocalVolume> volumes, Func<LocalVolume, VolumeMedia> media, IVolumeRetrim retrim)
    {
        foreach (var volume in volumes)
        {
            if (string.Equals(volume.RootPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return RetrimOffer.BasisOf(volume, media(volume)) is not null
                    ? new RetrimResult(root, retrim.Retrim(volume.VolumeName!))
                    : new RetrimResult(root, new RetrimAnswer(RetrimOutcome.NotOffered));
            }
        }

        return new RetrimResult(root, new RetrimAnswer(RetrimOutcome.NotOffered));
    }
}
