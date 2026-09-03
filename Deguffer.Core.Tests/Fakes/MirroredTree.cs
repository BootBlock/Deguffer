using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Tests.Fakes;

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
        var builder = new Builder();
        var scratch = Path.GetFullPath(temp.Path);

        builder.Add(builder.DescribeAncestorsOf(scratch), root, scratch);

        return (Path.Combine(scratch, root.Name), builder.Fixture);
    }

    private sealed class Builder
    {
        // NTFS reserves the first sixteen records for its own metadata, so entries start past them.
        private uint _next = MftRecord.ReservedRecordCount;

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
            var number = _next++;

            switch (entry)
            {
                case TreeFile file:
                    File.WriteAllBytes(path, new byte[file.Bytes]);

                    if (file.Resident)
                    {
                        Fixture.AddResidentFile(
                            number, parent, file.Name, file.Bytes, file.Created, file.Modified);
                    }
                    else
                    {
                        Fixture.AddFile(
                            number, parent, file.Name, file.Allocated ?? file.Bytes, logical: file.Bytes,
                            file.Created, file.Modified);
                    }

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
