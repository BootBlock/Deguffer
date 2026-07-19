using System.Text.RegularExpressions;

namespace Deguffer.Core.Safety;

/// <summary>
/// Recognises a directory by what it holds rather than by what it is called.
///
/// §5.2 classifies a child of a tool root by name and treats an unrecognised name as Tier 4. That
/// works while names carry meaning. Where a name is a hash it carries nothing that can be checked,
/// so the directory is unreachable by construction even when its contents are plainly regenerable.
///
/// A content signature is a <em>stronger</em> test than a name match, not a weaker one: it verifies
/// what a directory is instead of trusting what it is called. It must never be used to widen a name
/// list — a directory that fails the signature keeps the Tier 4 the name check gave it.
///
/// The match is deliberately total. The directory must hold at least one file, must hold no
/// subdirectories at all, and every entry must match. One unexpected entry disqualifies the whole
/// directory, because an unexpected entry is precisely the evidence that this is not the thing we
/// think it is. Only directory entries are read; no file is opened.
/// </summary>
public sealed class ContentSignature
{
    private readonly Regex _permitted;

    /// <param name="permitted">
    /// Matched against each file's name. Anchor it to the suffix rather than the whole name where
    /// the leading part is user-controlled.
    /// </param>
    public ContentSignature(Regex permitted)
    {
        ArgumentNullException.ThrowIfNull(permitted);
        _permitted = permitted;
    }

    /// <summary>
    /// Whether <paramref name="directory"/> holds nothing but files matching this signature.
    /// A directory that cannot be read does not match — §5.2's dangerous direction is treating
    /// an unknown thing as safe.
    /// </summary>
    public bool Matches(string directory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var sawFile = false;

        try
        {
            foreach (var entry in new DirectoryInfo(LongPath.Extended(directory)).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();

                // A subdirectory, a reparse point, or a file we do not recognise all mean this is
                // not the shape being looked for. Stop at the first one rather than reading the
                // rest of a directory that has already disqualified itself.
                if (entry is not FileInfo file || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }

                if (!_permitted.IsMatch(file.Name))
                {
                    return false;
                }

                sawFile = true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Cannot see inside it, so cannot vouch for it.
            return false;
        }

        // An empty directory matches nothing: there is no evidence either way, and "no evidence"
        // is not the same as "recognised".
        return sawFile;
    }
}
