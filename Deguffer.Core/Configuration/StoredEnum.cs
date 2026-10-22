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

    /// <summary>
    /// Where <paramref name="stored"/> sits in <paramref name="offered"/>, or the first place where it
    /// is not there. A caller lists the one safe reading of a missing value first.
    /// </summary>
    public static int IndexOf<T>(T stored, IReadOnlyList<T> offered)
        where T : struct, Enum
    {
        for (var i = 0; i < offered.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(offered[i], stored))
            {
                return i;
            }
        }

        return 0;
    }
}
