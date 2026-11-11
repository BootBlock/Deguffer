using Deguffer.Core.Safety;
using Deguffer.Core.Startup;

namespace Deguffer.Core.Exploring.Links;

/// <summary>
/// Reads a shortcut through the shell's own reader (§5.1 for a file: the format's owner reads it), and
/// a symbolic link or a junction from its reparse data, which Windows hands back without following it.
/// </summary>
public sealed class WindowsLinkTargetReader(IShortcutReader shortcuts) : ILinkTargetReader
{
    /// <summary>The one instance the app runs with (G5).</summary>
    public static WindowsLinkTargetReader Default { get; } = new(ShellShortcutReader.Default);

    private const uint MountPointTag = 0xA000_0003;
    private const uint SymbolicLinkTag = 0xA000_000C;

    /// <summary>The extension Windows gives a shortcut, and the only one the shell reads as one.</summary>
    public const string ShortcutExtension = ".lnk";

    /// <summary>Whether <paramref name="name"/> is a shortcut's, by its extension, as Explorer decides it.</summary>
    public static bool IsShortcutName(ReadOnlySpan<char> name) =>
        name.EndsWith(ShortcutExtension, StringComparison.OrdinalIgnoreCase);

    public LinkTarget? Read(string path, bool isLink)
    {
        if (!isLink)
        {
            return IsShortcutName(path) && shortcuts.Read(path) is { Target: { Length: > 0 } target }
                ? new LinkTarget(LinkKind.Shortcut, target)
                : null;
        }

        return KindOf(path) is { } kind && TargetOf(path) is { } recorded && Resolved(path, recorded) is { } resolved
            ? new LinkTarget(kind, resolved)
            : null;
    }

    /// <summary>
    /// Which link <paramref name="path"/> is, from its tag, read through a handle opened on the link
    /// itself. Null for any other reparse point: a cloud placeholder or a deduplicated file names no
    /// target, and nothing about one is a link left behind.
    /// </summary>
    private static LinkKind? KindOf(string path)
    {
        using var handle = FileInformation.Open(LongPath.Extended(path), HandleUse.Describe);

        if (handle.IsInvalid || !FileInformation.TryAttributeTag(handle, out var tag))
        {
            return null;
        }

        return tag.ReparseTag switch
        {
            MountPointTag => LinkKind.Junction,
            SymbolicLinkTag => LinkKind.SymbolicLink,
            _ => null,
        };
    }

    /// <summary>
    /// The target the link records, read without following it. A link Windows will not describe, or one
    /// gone since the listing, records nothing this can read.
    /// </summary>
    private static string? TargetOf(string path)
    {
        try
        {
            var extended = LongPath.Extended(path);
            FileSystemInfo link = (File.GetAttributes(extended) & FileAttributes.Directory) != 0
                ? new DirectoryInfo(extended)
                : new FileInfo(extended);

            return link.LinkTarget is { Length: > 0 } target ? target : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="recorded"/> as a full path in display form, or null where it cannot be placed. A
    /// junction records the object manager's <c>\??\</c> form, which <see cref="LongPath.Display"/>
    /// reads as the device form it is.
    ///
    /// <para><b>A relative symbolic link is resolved from where its folder really is.</b> Windows
    /// follows every link above it first, so on a profile moved to <c>D:\</c> by a junction, a link
    /// recording <c>..\..\Tools\app.exe</c> opens <c>D:\...\Tools\app.exe</c>. Resolved from the path as
    /// written, it would name a path on <c>C:\</c> that is not there, and a working link would read as
    /// broken. A folder Windows will not open gives no place to resolve from, and the link claims
    /// nothing.</para>
    /// </summary>
    private static string? Resolved(string path, string recorded)
    {
        if (Path.IsPathFullyQualified(recorded) || recorded.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return LongPath.Display(recorded);
        }

        return Path.GetDirectoryName(LongPath.Display(path)) is { Length: > 0 } folder
               && FileInformation.Default.FinalPath(folder) is { } final
            ? LongPath.Display(Path.GetFullPath(recorded, LongPath.Display(final)))
            : null;
    }
}
