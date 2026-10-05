using Deguffer.Core.Configuration;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Scanning;

/// <summary>
/// The values one scan of one volume runs with: the user's number where they chose one, and
/// <see cref="AutoTuning"/>'s for the kind of drive where they left it on Auto.
/// </summary>
/// <param name="Media">The kind of storage the values were chosen for.</param>
/// <param name="Chosen">What the user set for that kind, where each null is Auto.</param>
public sealed record VolumeTuning(StorageMedia Media, MediaScanPreferences Chosen, WalkTuning Walk, TableTuning Table)
{
    /// <summary>
    /// What a scan of <paramref name="media"/> runs with under <paramref name="preferences"/>.
    ///
    /// <para><b>Every number is clamped here, as it is read.</b> The settings box clamps what is
    /// typed, but nothing validates <c>preferences.json</c> on the way in, and a hand-edited value
    /// outside the bounds would otherwise throw out of a scan. Sizes are clamped in KiB before they
    /// become bytes, so a hand-edited size near <see cref="int.MaxValue"/> cannot overflow.</para>
    /// </summary>
    public static VolumeTuning Resolve(ScanPreferences preferences, StorageMedia media)
    {
        var chosen = preferences.For(media);

        var threads = Math.Clamp(
            chosen.WalkThreads ?? AutoTuning.WalkThreads(media),
            WalkTuning.MinimumThreads,
            WalkTuning.MaximumThreads);

        var listingKiB = Math.Clamp(
            chosen.ListingBufferKiB ?? AutoTuning.ListingBufferKiB(media),
            WalkTuning.MinimumListingBuffer / 1024,
            WalkTuning.MaximumListingBuffer / 1024);

        var tableKiB = Math.Clamp(
            chosen.TableReadKiB ?? AutoTuning.TableReadKiB(media),
            TableTuning.MinimumReadBytes / 1024,
            TableTuning.MaximumReadBytes / 1024);

        return new VolumeTuning(
            media,
            chosen,
            new WalkTuning(threads, listingKiB * 1024),
            new TableTuning(tableKiB * 1024));
    }
}
