namespace Deguffer.Core.Safety;

/// <summary>
/// Whether one walk still reads link counts and sizes by name (<see cref="StatByName"/>), or has
/// turned to a handle per file because its file system does not answer the class.
///
/// <para>One per walk, because a walk stays on one volume: it never descends through a reparse
/// point, which is what a folder a volume is mounted at is. A file system that refused the class for
/// one file refuses it for every other, and asking each of a million files again would cost the call
/// that failed on top of the handle that answers.</para>
/// </summary>
internal sealed class ByNameRoute
{
    private bool _declined;

    /// <summary>Whether a file system has refused the class during this walk, read from any of its threads.</summary>
    public bool Declined => Volatile.Read(ref _declined);

    public void Decline() => Volatile.Write(ref _declined, true);
}
