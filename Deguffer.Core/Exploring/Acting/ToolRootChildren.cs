using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// §5.2 for one tool root, as one <see cref="ExploreActionPolicy.MayRemove"/> asks it: the root is
/// never a target, and below it the first segment decides, by its name and by what it is on disk,
/// because the provider's plan judges both.
///
/// <para>The kind is read from the disk rather than taken from the item being removed, because the
/// child is the first segment below the root and the item may be deep inside it: whether
/// <c>shadercache\440\a.bin</c> may go depends on what <c>440</c> is, and the row being removed
/// says only what <c>a.bin</c> is.</para>
///
/// <para>One instance serves one question, and holds each reading for its life (G4): several roots
/// can be asked about the same child, and the entry is not read again for each of them.</para>
/// </summary>
internal sealed class ToolRootChildren(IFileSystem fileSystem)
{
    private static readonly char[] Separators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private readonly Dictionary<string, (PathPresence Presence, ChildKind Kind)> _readings =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Why <paramref name="root"/> refuses <paramref name="target"/>, or null where it recognises
    /// the child the target is in.
    ///
    /// <para>The first segment and not the last, because that is the segment the provider
    /// classified. <c>.gradle\caches\modules-2</c> is inside a recognised child and goes with it,
    /// and <c>.gradle\init.d\anything</c> is inside an unrecognised one and does not — asking about
    /// the leaf instead would refuse the first and allow the second, which is exactly backwards.</para>
    /// </summary>
    /// <param name="root">A root containing <paramref name="target"/>, already established by the caller.</param>
    /// <param name="target">The item, named below the root's own path as <see cref="DeclaredRoot.Naming"/> names it.</param>
    public ExploreVerdict? Refusal(DeclaredRoot root, string target)
    {
        var rootPath = root.Path;

        if (target.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return ExploreVerdict.Refuse(root.Root.Reason);
        }

        // Empty only if the remainder is separators alone, which Configured has already collapsed
        // into the equality above. Read as a refusal rather than indexed blindly: this is the one
        // predicate standing between a size picture and a tool's credentials.
        if (target[rootPath.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            is not [var child, ..])
        {
            return ExploreVerdict.Refuse(root.Root.Reason);
        }

        // A file or a link named like a recognised folder is refused here rather than allowed by its
        // name: the plan lists folders, or declines links, and so never offered it (§7.1).
        return Recognises(root.Root, rootPath, child) switch
        {
            true => null,
            null => ExploreVerdict.Refuse(
                $"Windows would not say what '{child}' inside '{rootPath}' is, so Deguffer cannot tell "
                + "whether it is something it recognises there, and leaves it alone."),
            false => ExploreVerdict.Refuse(
                $"'{child}' is not something Deguffer recognises inside '{rootPath}'. Configuration "
                + "and credentials sit beside a cache in a tool's own folder, so anything unrecognised "
                + "there is left alone."),
        };
    }

    /// <returns>Null where Windows would not say what the entry is.</returns>
    private bool? Recognises(ToolRoot root, string rootPath, string child)
    {
        // Joined onto the extended root rather than extended afterwards, because normalising the
        // whole path drops a trailing space from its last segment: a child named 'x ' would be read
        // as its sibling 'x', while the removal acts on 'x '.
        var (presence, kind) = Read(Path.Join(LongPath.Extended(rootPath), child));

        return presence switch
        {
            PathPresence.Present => root.Recognises(new ToolRootChild(child, kind)),

            // Nothing is there, so nothing can be removed and the kind decides nothing. The name
            // decides, for either entry a plan could offer, which reports a row that vanished the
            // way a vanished row anywhere else is reported.
            PathPresence.Absent => root.Recognises(new ToolRootChild(child, ChildKind.Folder))
                || root.Recognises(new ToolRootChild(child, ChildKind.File)),

            // An entry Windows will not describe could be a link as easily as a folder, and the plan
            // classified neither, so it is never read as either.
            _ => null,
        };
    }

    private (PathPresence Presence, ChildKind Kind) Read(string extended)
    {
        if (_readings.TryGetValue(extended, out var reading))
        {
            return reading;
        }

        reading = fileSystem.TryGetAttributes(extended) switch
        {
            // Gone and unreadable both answer null there, and only the second must refuse.
            null => (fileSystem.MayExist(extended) ? PathPresence.Refused : PathPresence.Absent, default),
            var attributes when attributes.Value.HasFlag(FileAttributes.ReparsePoint) => (PathPresence.Present, ChildKind.Link),
            var attributes when attributes.Value.HasFlag(FileAttributes.Directory) => (PathPresence.Present, ChildKind.Folder),
            _ => (PathPresence.Present, ChildKind.File),
        };

        return _readings[extended] = reading;
    }
}
