namespace Deguffer.Core.Configuration;

/// <summary>
/// An enum read from the preferences file. The file is read with numbers allowed for an enum, so a
/// hand-edited value can arrive that no member names, and a stray value in a cosmetic setting must
/// never stop the app working (see <see cref="PreferenceStore.Load"/>).
/// </summary>
internal static class StoredEnum
{
    /// <summary><paramref name="value"/> where a member names it, otherwise <paramref name="fallback"/>.</summary>
    public static T Defined<T>(T value, T fallback)
        where T : struct, Enum =>
        Enum.IsDefined(value) ? value : fallback;
}
