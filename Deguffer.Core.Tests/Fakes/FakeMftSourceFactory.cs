using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Stands in for the volume opener, so route selection can be tested both ways round without
/// administrator rights and without depending on what the build agent's disk happens to be.
/// </summary>
public sealed class FakeMftSourceFactory : IMftSourceFactory
{
    private readonly Func<char, IMftSource?> _open;
    private readonly FallbackReason _reason;

    private FakeMftSourceFactory(Func<char, IMftSource?> open, FallbackReason reason)
    {
        _open = open;
        _reason = reason;
    }

    /// <summary>How many times a volume was opened — the assertion that the index is cached.</summary>
    public int OpenCount { get; private set; }

    /// <summary>A machine where the fast path is unavailable, for the stated reason.</summary>
    public static FakeMftSourceFactory Unavailable(FallbackReason reason) => new(_ => null, reason);

    /// <summary>A machine where exactly one volume serves the given table.</summary>
    public static FakeMftSourceFactory Serving(char driveLetter, MftFixture fixture) =>
        new(letter => letter == driveLetter ? fixture.Build() : null, FallbackReason.NotNtfsVolume);

    public IMftSource? TryOpen(char driveLetter, out FallbackReason reason)
    {
        OpenCount++;

        var source = _open(driveLetter);
        reason = source is null ? _reason : FallbackReason.None;

        return source;
    }
}
