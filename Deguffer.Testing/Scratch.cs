namespace Deguffer.Testing;

/// <summary>
/// What the suite names its own scratch, and how long a piece of it may live before a later run
/// collects it. Both sweeps read these, so one margin and one recogniser govern the directories
/// under TEMP and the keys under HKCU alike.
/// </summary>
internal static class Scratch
{
    /// <summary>
    /// How old a piece of scratch has to be before a sweep will touch it.
    ///
    /// <para>The margin is what keeps a sweep off scratch another process is still using: a
    /// concurrent run would have to have been going for an hour for its leavings to qualify, and
    /// this suite finishes in minutes. It is the only thing standing between a live run and a
    /// delete, because a name alone cannot tell an abandoned piece of scratch from a busy one.</para>
    /// </summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    /// <summary>A fresh identifier for one piece of this run's scratch.</summary>
    internal static string NewIdentifier() => Guid.NewGuid().ToString("N");

    /// <summary>Whether <paramref name="name"/> is an identifier this suite writes.</summary>
    /// <remarks>
    /// The round trip, rather than <see cref="Guid.TryParseExact(string, string, out Guid)"/> alone,
    /// which trims its input and accepts either case. Leading whitespace and upper-case hex are both
    /// names <see cref="NewIdentifier"/> never writes, and §5.2 turns on recognising only what we
    /// made.
    /// </remarks>
    internal static bool IsIdentifier(string name) =>
        Guid.TryParseExact(name, "N", out var id)
        && id.ToString("N").Equals(name, StringComparison.Ordinal);
}
