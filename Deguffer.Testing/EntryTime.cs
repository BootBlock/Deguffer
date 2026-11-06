using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// Dates a file or directory a test made, by both of the times Deguffer reads.
///
/// <para>The age column and the minimum-age guard both read the newer of an entry's creation and last
/// write. An entry a test has just made was created now, so setting its last write alone leaves it
/// dated now whatever the test meant. Setting both is how a test makes an entry as old as it says.</para>
/// </summary>
public static class EntryTime
{
    /// <summary>Make <paramref name="path"/> created and last written at <paramref name="utc"/>.</summary>
    public static void Set(string path, DateTime utc) => Set(path, created: utc, written: utc);

    /// <summary>
    /// Make <paramref name="path"/> created at <paramref name="created"/> and last written at
    /// <paramref name="written"/>, for a test about which of the two an age is read from.
    /// </summary>
    public static void Set(string path, DateTime created, DateTime written)
    {
        var extended = LongPath.Extended(path);

        // A directory's own times matter to a provider that judges an empty folder by them, and
        // the File calls refuse a directory.
        if (Directory.Exists(extended))
        {
            Directory.SetCreationTimeUtc(extended, created);
            Directory.SetLastWriteTimeUtc(extended, written);
        }
        else
        {
            File.SetCreationTimeUtc(extended, created);
            File.SetLastWriteTimeUtc(extended, written);
        }
    }
}
