using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Testing;

/// <summary>
/// An opened volume whose table is a source a test built already: what a test's
/// <see cref="IMftSourceFactory"/> hands back where the step between the open and the table is not
/// what the test is about. Owns the source until <see cref="TryReadTable"/> hands it over, as a real
/// volume owns its handle.
/// </summary>
public sealed class ServedVolume(IMftSource source) : IMftVolume
{
    private IMftSource? _source = source;

    public IMftSource? TryReadTable(out FallbackReason reason)
    {
        reason = FallbackReason.None;

        return Interlocked.Exchange(ref _source, null)
            ?? throw new InvalidOperationException("The table of this volume was read already, or the volume was closed.");
    }

    public void Dispose() => Interlocked.Exchange(ref _source, null)?.Dispose();
}
