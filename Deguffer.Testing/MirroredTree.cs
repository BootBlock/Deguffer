using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Testing;

/// <summary>One entry in a described tree.</summary>
public abstract record TreeEntry(string Name)
{
    /// <summary>
    /// When the entry should claim to have been made — stamped onto the real file and declared to
    /// the table, so the two routes are asked about one instant rather than two that resemble each
    /// other. Null leaves the real entry with whatever the clock said and the table with the zero
    /// NTFS writes for a time it never set, which is a deliberate <em>dis</em>agreement and not
    /// something to assert on.
    /// </summary>
    public DateTime? Created { get; init; }

    /// <summary>When it should claim to have been last written, on the same terms.</summary>
    public DateTime? Modified { get; init; }
}

/// <param name="Bytes">The file's length, written to disk and declared to the table.</param>
/// <param name="Allocated">
/// What the table says the file occupies, where that differs from its length. Null means the two
/// agree, which is the ordinary case.
///
/// <para>A walk can only ever report the length: it reads <c>FileInfo.Length</c> and says so by
/// setting allocated equal to logical. The table knows both. So a fixture where the two agree
/// cannot tell a caller that mixes the axes from one that does not, and every mirrored tree agreed
/// until this existed. Cluster slack makes allocated larger on any real volume; NTFS compression
/// makes it smaller.</para>
/// </param>
/// <param name="Resident">
/// Whether the table should describe this file as living inside its own MFT record. A real file
/// written to disk may be resident or not depending on its size and the volume, and no test can
/// control that — stating it here is what lets one pin the single place the two routes are
/// expected to disagree.
/// </param>
public sealed record TreeFile(string Name, int Bytes, bool Resident = false, int? Allocated = null) : TreeEntry(Name);

public sealed record TreeDirectory(string Name, params TreeEntry[] Children) : TreeEntry(Name);

/// <summary>
/// Another name for a file described earlier in the same tree: a hard link, made on disk as
/// <c>mklink /H</c> makes one, and declared to the table as one more <c>$FILE_NAME</c> in that file's
/// record. It has no dates of its own, because a link is the file.
/// </summary>
/// <param name="Target">
/// The file this is another name of, as components below the tree's root. It must be described
/// before this entry, so the file exists when the link is made.
/// </param>
public sealed record TreeLink(string Name, params string[] Target) : TreeEntry(Name);

/// <summary>
/// One tree, described once and built twice: as real directories and files under a scratch root,
/// and as the MFT records that would describe those same paths.
///
/// §5.5's two routes are meant to answer the same questions, and nothing in the suite compared them
/// against one subject until this existed. Generating both from a single description is what stops
/// such a comparison drifting into two trees that merely look alike — at which point it would still
/// pass while proving nothing.
/// </summary>
public static class MirroredTree
{
    /// <summary>
    /// Build <paramref name="root"/> under <paramref name="temp"/>, and a table describing it.
    ///
    /// The chain of directories above the tree is described too, so the index resolves the same
    /// absolute path the walk is handed rather than a path that merely ends the same way.
    /// </summary>
    public static (string Path, MftFixture Fixture) Realise(TempDirectory temp, TreeDirectory root)
    {
        var scratch = Path.GetFullPath(temp.Path);
        var path = Path.Combine(scratch, root.Name);
        var builder = new Builder(path);

        builder.Add(builder.DescribeAncestorsOf(scratch), root, scratch);
        builder.DescribeFiles();

        return (path, builder.Fixture);
    }

    /// <param name="root">The tree's own folder, which a link's target is named below.</param>
    private sealed class Builder(string root)
    {
        // NTFS reserves the first sixteen records for its own metadata, so entries start past them.
        private uint _next = MftRecord.ReservedRecordCount;

        // Each file's record is written once the whole tree is described, because a link met later
        // adds a name to it. Keyed by the file's path.
        private readonly Dictionary<string, (uint Number, TreeFile File, List<(uint Parent, string Name)> Names)> _files =
            new(StringComparer.OrdinalIgnoreCase);

        public MftFixture Fixture { get; } = new();

        /// <summary>The record number of <paramref name="directory"/>, describing every level above it.</summary>
        public uint DescribeAncestorsOf(string directory)
        {
            var parent = MftRecord.RootRecordNumber;

            // Past the drive letter, colon and separator: the table knows nothing about volumes.
            foreach (var component in directory[3..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                Fixture.AddDirectory(_next, parent, component);
                parent = _next++;
            }

            return parent;
        }

        public void Add(uint parent, TreeEntry entry, string parentPath)
        {
            var path = Path.Combine(parentPath, entry.Name);

            // A link is another name in a file's record, and takes no record of its own. Made as it
            // is met, inside the folder it is listed in, so the folder's own dates are stamped after
            // it as they are after every other child.
            if (entry is TreeLink link)
            {
                var existing = Path.Combine([root, .. link.Target]);

                HardLink.To(existing, path);
                _files[existing].Names.Add((parent, link.Name));
                return;
            }

            var number = _next++;

            switch (entry)
            {
                case TreeFile file:
                    File.WriteAllBytes(path, new byte[file.Bytes]);
                    _files[path] = (number, file, [(parent, file.Name)]);

                    Stamp(file, File.SetCreationTimeUtc, File.SetLastWriteTimeUtc, path);
                    break;

                case TreeDirectory directory:
                    Directory.CreateDirectory(path);
                    Fixture.AddDirectory(
                        number, parent, directory.Name, directory.Created, directory.Modified);

                    foreach (var child in directory.Children)
                    {
                        Add(number, child, path);
                    }

                    // After the children, never before. Creating an entry inside a directory moves
                    // that directory's own last-written time, so a stamp applied first would be
                    // overwritten by the first child and the two routes would disagree about a
                    // date the description said they should share.
                    Stamp(directory, Directory.SetCreationTimeUtc, Directory.SetLastWriteTimeUtc, path);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(entry), entry, "Unknown tree entry.");
            }
        }

        /// <summary>
        /// Declare every file in the tree to the table, with each of its names.
        /// </summary>
        public void DescribeFiles()
        {
            foreach (var (path, (number, file, names)) in _files)
            {
                if (names.Count > 1)
                {
                    if (file.Resident)
                    {
                        throw new NotSupportedException($"{path} is described as resident, which no hard-linked file here is.");
                    }

                    Fixture.AddHardLinkedFile(
                        number, names, file.Allocated ?? file.Bytes, logical: file.Bytes, file.Created, file.Modified);
                }
                else if (file.Resident)
                {
                    Fixture.AddResidentFile(
                        number, names[0].Parent, file.Name, file.Bytes, file.Created, file.Modified);
                }
                else
                {
                    Fixture.AddFile(
                        number, names[0].Parent, file.Name, file.Allocated ?? file.Bytes, logical: file.Bytes,
                        file.Created, file.Modified);
                }
            }
        }

        /// <summary>
        /// Put the described dates onto the real entry, where it described any.
        ///
        /// <para>Files and directories are stamped by different pairs of methods with identical
        /// signatures, which is the whole reason this takes them as arguments rather than switching
        /// on what it was handed.</para>
        /// </summary>
        private static void Stamp(
            TreeEntry entry,
            Action<string, DateTime> setCreated,
            Action<string, DateTime> setLastWritten,
            string path)
        {
            if (entry.Created is { } created)
            {
                setCreated(path, created);
            }

            if (entry.Modified is { } modified)
            {
                setLastWritten(path, modified);
            }
        }
    }
}
