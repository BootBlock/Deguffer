using System.Buffers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// The paths a <see cref="RemovalBounds"/> holds back, filed under the folder each sits directly in, so
/// a removal's walk asks once per folder whether anything in it is held back, and only of a folder that
/// holds one asks each entry by its name.
///
/// <para><b>Filed by folder because almost nothing a walk meets is held back.</b> Asked by full path,
/// every entry of a bounded removal (a <c>%TEMP%</c> of half a million entries) would need its path
/// built to look up and then dropped. Asked by folder, a folder holding no bound path costs one lookup
/// of the string its listing already has, and a folder that holds one looks each child up by the name
/// the listing read, through a span, so neither builds a string (G4).</para>
///
/// <para><b>Both sides are compared without their trailing separators.</b> The listing of a volume's
/// top is <c>\\?\C:\</c>, with its separator, while the folder a path below it sits in, cut from the
/// path, is <c>\\?\C:</c>. A root handed in as <c>C:\Temp\</c> is listed with its separator too, and a
/// bound written <c>C:\Temp\session\</c> names the entry <c>session</c>. Matching the spellings
/// exactly would hold none of those back, which fails in the direction §5.2 forbids: every one would be
/// deleted.</para>
/// </summary>
internal sealed class BoundPaths
{
    private static readonly char[] SeparatorChars = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private static readonly SearchValues<char> Separators = SearchValues.Create(SeparatorChars);

    private readonly Dictionary<string, Folder> _folders = new(StringComparer.OrdinalIgnoreCase);

    // Looked up by a span cut from a listing's own string, so a folder is asked about with no string built.
    private readonly Dictionary<string, Folder>.AlternateLookup<ReadOnlySpan<char>> _foldersBySpan;

    /// <param name="userWork">Paths held back because they hold somebody's work, in display or extended form.</param>
    /// <param name="spared">Paths held back because something is using them.</param>
    /// <param name="ownedElsewhere">Paths held back because another row offers them.</param>
    public BoundPaths(IReadOnlyList<string> userWork, IReadOnlyList<string> spared, IReadOnlyList<string> ownedElsewhere)
    {
        _foldersBySpan = _folders.GetAlternateLookup<ReadOnlySpan<char>>();

        // Filed in the order a walk asks, so a path given for two reasons is held back for the first:
        // an editor's unsaved sketch is still the user's work once nothing is using it.
        File(userWork, BoundReason.UserWork);
        File(spared, BoundReason.Spared);
        File(ownedElsewhere, BoundReason.OwnedElsewhere);
    }

    /// <summary>Whether no path is held back, so a walk need ask nothing of any entry.</summary>
    public bool IsEmpty => _folders.Count == 0;

    /// <summary>
    /// The entries held back directly inside <paramref name="directory"/>, or null where it holds none.
    /// </summary>
    /// <param name="directory">The folder as its listing spells it, in extended form.</param>
    public Folder? In(string directory) =>
        _foldersBySpan.TryGetValue(WithoutTrailingSeparators(directory), out var folder) ? folder : null;

    /// <summary>Why the entry at <paramref name="extended"/> is held back, if it is.</summary>
    public BoundReason Of(string extended) =>
        TrySplit(extended, out var parent, out var name) && _foldersBySpan.TryGetValue(parent, out var folder)
            ? folder.Of(name)
            : BoundReason.None;

    private void File(IReadOnlyList<string> paths, BoundReason reason)
    {
        foreach (var path in paths)
        {
            var extended = LongPath.Extended(path);

            if (!TrySplit(extended, out var parent, out var name))
            {
                // A volume's top: nothing a walk lists is one, so no entry could be held back by it.
                continue;
            }

            if (!_foldersBySpan.TryGetValue(parent, out var folder))
            {
                folder = new Folder();
                _folders.Add(parent.ToString(), folder);
            }

            folder.Add(name, reason);
        }
    }

    /// <summary>
    /// Cut <paramref name="extended"/> at its last separator into the folder it sits in and its name.
    /// False where it names a folder with nothing above it, such as <c>\\?\C:\</c> or a share's top.
    /// </summary>
    private static bool TrySplit(string extended, out ReadOnlySpan<char> parent, out ReadOnlySpan<char> name)
    {
        var path = WithoutTrailingSeparators(extended);
        var last = path.LastIndexOfAny(Separators);

        if (last < 0 || Path.GetPathRoot(path).Length >= path.Length)
        {
            parent = default;
            name = default;
            return false;
        }

        parent = WithoutTrailingSeparators(path[..last]);
        name = path[(last + 1)..];
        return true;
    }

    private static ReadOnlySpan<char> WithoutTrailingSeparators(ReadOnlySpan<char> path) =>
        path.TrimEnd(SeparatorChars);

    /// <summary>The entries held back directly inside one folder, asked by the name a listing read.</summary>
    internal sealed class Folder
    {
        private readonly Dictionary<string, BoundReason> _names = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, BoundReason>.AlternateLookup<ReadOnlySpan<char>> _namesBySpan;

        public Folder() => _namesBySpan = _names.GetAlternateLookup<ReadOnlySpan<char>>();

        /// <summary>Why the entry called <paramref name="name"/> is held back, if it is.</summary>
        public BoundReason Of(ReadOnlySpan<char> name) =>
            _namesBySpan.TryGetValue(name, out var reason) ? reason : BoundReason.None;

        public void Add(ReadOnlySpan<char> name, BoundReason reason) => _namesBySpan.TryAdd(name, reason);
    }
}
