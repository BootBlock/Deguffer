using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Testing;

/// <summary>
/// Volumes whose opens wait in one <see cref="Company"/> for another volume's, so two volumes read at
/// the same time show as two opens held at once (<see cref="MostAtOnce"/>). A table is opened on the
/// thread that first asks for it, so two opens held at once is only possible where two volumes were
/// worked on together.
/// </summary>
/// <param name="wait">How long an open waits for another volume's. See <see cref="Company"/>.</param>
/// <param name="volumes">Each volume served, by its letter. Any other letter is not NTFS.</param>
public sealed class CompanyMftSourceFactory(TimeSpan wait, params (char Letter, MftFixture Fixture)[] volumes) : IMftSourceFactory
{
    private readonly Company _opening = new();
    private int _opened;

    /// <summary>The most opens of served volumes that were held at once.</summary>
    public int MostAtOnce => _opening.MostAtOnce;

    /// <summary>How many opens were asked for, of any letter.</summary>
    public int OpenCount => Volatile.Read(ref _opened);

    public IMftVolume? TryOpen(char driveLetter, out FallbackReason reason, MftOpenTimings? timings = null)
    {
        Interlocked.Increment(ref _opened);

        var letter = char.ToUpperInvariant(driveLetter);

        if (!volumes.Any(volume => volume.Letter == letter))
        {
            reason = FallbackReason.NotNtfsVolume;
            return null;
        }

        _opening.Wait(wait);

        reason = FallbackReason.None;
        return new ServedVolume(volumes.Single(volume => volume.Letter == letter).Fixture.Build());
    }
}
