namespace Deguffer.Core.Safety;

/// <summary>
/// What a guard decided about one file when the file was measured, and which guard decided it.
///
/// <para><b>The guard is part of the answer.</b> A plan withdraws a recent file's step under the guard
/// in force, which is the stricter of the user's and the provider's own. A provider that measured under
/// the user's guard alone has an answer to a different question, so the verdict answers only the guard
/// it was given under, and the plan reads the file again for any other. That keeps the withdrawal the
/// backstop it is, which holds even where a provider forgets its own floor.</para>
/// </summary>
/// <param name="Guard">The guard the file was measured under.</param>
/// <param name="Keeps">Whether <paramref name="Guard"/> keeps the file, by the file's own times.</param>
public readonly record struct FileAgeVerdict(MinimumAge Guard, bool Keeps)
{
    /// <summary>Whether <paramref name="guard"/> keeps the file, or null where this verdict was given under another guard.</summary>
    public bool? Under(MinimumAge guard) => Guard == guard ? Keeps : null;
}
