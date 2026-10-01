namespace Deguffer.Core.Providers;

/// <summary>
/// A settings or record file a provider needed and could not read, kept as one of the two shapes
/// that refusal takes.
///
/// <para><b>The two shapes may not share a sentence</b>, for the reason <see cref="UnreadableRoot"/>
/// gives for a directory. A file whose read failed is certainly there. A file a probe by name could
/// not reach is not even known to exist, so "could not read" would assert something nobody
/// established. See <see cref="Safety.PathPresence"/>.</para>
/// </summary>
/// <param name="Path">The file, named in the sentence the user is shown.</param>
/// <param name="Unreached">
/// Windows would not say whether the file is there. False where it is there and would not be read:
/// locked by another program, or refused to this account.
/// </param>
public sealed record UnreadFile(string Path, bool Unreached)
{
    /// <summary>
    /// The opening of the sentence about the file, naming it as <paramref name="what"/>. The caller
    /// says what followed from it.
    /// </summary>
    public string Opening(string what) => Unreached
        ? $"Windows would not say whether {what} is at '{Path}'"
        : $"Deguffer could not read {what} at '{Path}'";
}
