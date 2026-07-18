using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// A local path split into the volume that holds it and the components below that volume's root —
/// the form an MFT lookup needs, since the table knows nothing about drive letters.
/// </summary>
public readonly record struct VolumePath(char DriveLetter, IReadOnlyList<string> Components)
{
    /// <summary>
    /// Parse a rooted local path. Returns false for anything the MFT route cannot serve — UNC
    /// paths, volumes mounted without a letter, and relative paths — which is the signal to fall
    /// back rather than an error.
    /// </summary>
    public static bool TryParse(string path, out VolumePath result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            // Strip any extended-length prefix first: §6.3 puts every path in Core through
            // LongPath, so most arrive here as \\?\C:\... and the raw form would fail the
            // drive-letter check below.
            full = Path.GetFullPath(LongPath.Display(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (full.Length < 3 || full[1] != ':' || !char.IsAsciiLetter(full[0]))
        {
            return false;
        }

        result = new VolumePath(
            char.ToUpperInvariant(full[0]),
            full[2..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));

        return true;
    }
}
