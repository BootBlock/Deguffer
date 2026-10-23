namespace Deguffer.Core.Scanning;

/// <summary>
/// What a planning pass keeps alive for its length, given back once when the pass ends. See
/// <see cref="IDirectoryScanner.Hold"/>.
///
/// <para>Ending it twice gives back once. A holder that counts its holds would otherwise count one
/// hold's end twice, and drop what another hold of the same pass still keeps.</para>
/// </summary>
public sealed class PassHold : IDisposable
{
    /// <summary>The hold of something that keeps nothing between calls.</summary>
    public static PassHold Nothing { get; } = new(static () => { });

    private Action? _release;

    /// <param name="release">Gives back what was held, run by the first <see cref="Dispose"/> alone.</param>
    public PassHold(Action release)
    {
        ArgumentNullException.ThrowIfNull(release);

        _release = release;
    }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
