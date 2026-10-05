using Deguffer.Core.Safety;

namespace Deguffer.Benchmark;

/// <summary>
/// Where a result was measured, in the only terms a result may name it: the drive letter, the kind
/// of drive and its file system.
///
/// <para>A result is meant to be pasted into a public issue as it stands, and a path names a user,
/// a project or a share. So the path is reduced to these three facts here, once, and the report is
/// only ever handed this. Nothing downstream can print what it was never given.</para>
/// </summary>
/// <param name="Root">The drive letter with its colon, or <see cref="NoLetter"/>.</param>
internal sealed record MeasuredPlace(string Root, DriveType Kind, string FileSystem)
{
    /// <summary>
    /// Said of a share reached by its server's name, or a volume reached by its GUID. Both of those
    /// name something this must never repeat.
    /// </summary>
    public const string NoLetter = "no drive letter";

    public const string UnknownFileSystem = "unknown file system";

    /// <summary>
    /// The drive letter of <paramref name="path"/>, such as <c>C:</c>, or <see cref="NoLetter"/>.
    /// The extended prefix comes off first, so <c>\\?\C:\</c> is still on <c>C:</c>.
    /// </summary>
    public static string RootOf(string path)
    {
        var root = Path.GetPathRoot(LongPath.Display(path));

        return root is { Length: >= 2 } && root[1] == ':' && char.IsAsciiLetter(root[0])
            ? $"{char.ToUpperInvariant(root[0])}:"
            : NoLetter;
    }

    /// <summary>Describe the drive <paramref name="path"/> is on, asking Windows what kind it is.</summary>
    public static MeasuredPlace Of(string path)
    {
        var root = RootOf(path);
        if (root == NoLetter)
        {
            var display = LongPath.Display(path);
            var share = display.StartsWith(@"\\", StringComparison.Ordinal)
                && !display.StartsWith(@"\\?\", StringComparison.Ordinal);

            return new MeasuredPlace(root, share ? DriveType.Network : DriveType.Unknown, UnknownFileSystem);
        }

        var drive = new DriveInfo(root);

        try
        {
            return new MeasuredPlace(root, drive.DriveType, drive.DriveFormat);
        }

        // A drive that is not ready, or one this account may not query, still has a letter and a
        // kind worth reporting. Only its file system is lost.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MeasuredPlace(root, drive.DriveType, UnknownFileSystem);
        }
    }

    public override string ToString() => $"{Root} ({Kind}, {FileSystem})";
}
