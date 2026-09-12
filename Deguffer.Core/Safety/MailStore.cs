namespace Deguffer.Core.Safety;

/// <summary>
/// Outlook's mail stores: an offline mailbox (<c>.ost</c>) or a personal data file (<c>.pst</c>).
/// Deguffer never removes one, by any route (§9).
///
/// <para><b>A rule about a type of file, because no rule about a place can find one.</b> A
/// <c>.pst</c> is wherever somebody saved it: an archive on a data disk, a folder they named, a
/// temporary folder an archive was opened from, a build directory. Every other exclusion Deguffer makes
/// is an address, and an address is the wrong shape for this. So the removals, the measurements that
/// forecast them and the plan that names what stays all ask this one question of every file they
/// meet, and there is one place to change the answer.</para>
///
/// <para>The name is matched on its extension and nothing else, so <c>archive.pst.txt</c> and
/// <c>archive.pstx</c> are ordinary files. The question is asked of files and never of a link: a link
/// is removed as a link, and removing one leaves what it points at exactly where it was.</para>
/// </summary>
public static class MailStore
{
    /// <summary>Whether <paramref name="nameOrPath"/> names an Outlook mail store.</summary>
    /// <param name="nameOrPath">A file name, or a path in any form; only its extension is read.</param>
    public static bool Is(ReadOnlySpan<char> nameOrPath)
    {
        var extension = Path.GetExtension(nameOrPath);

        return extension.Equals(".ost", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pst", StringComparison.OrdinalIgnoreCase);
    }
}
