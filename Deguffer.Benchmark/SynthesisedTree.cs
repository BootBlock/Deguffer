using Deguffer.Core.Safety;

namespace Deguffer.Benchmark;

/// <summary>
/// Builds a tree of a given <see cref="TreeShape"/> for a removal route to remove. Never timed: a run
/// is timed from the removal's start, and the tree is built before it.
/// </summary>
internal static class SynthesisedTree
{
    /// <summary>
    /// Build <paramref name="shape"/> at <paramref name="root"/>. Folders are named <c>d0</c>,
    /// <c>d1</c> and on in each folder, and files <c>f0</c>, <c>f1</c> and on across the tree, so no
    /// two runs build different trees from the same shape.
    ///
    /// <para>Where an earlier run left a file of the tree standing, the build fails on it with an
    /// <see cref="IOException"/>, rather than go on to time a tree that is not the shape it names.</para>
    /// </summary>
    public static void Build(string root, TreeShape shape, CancellationToken ct)
    {
        // §6.3: a deep shape is past MAX_PATH long before its files, and a folder cut short there would
        // build a smaller tree than the result names.
        var folders = new List<string>(checked((int)shape.Folders + 1)) { LongPath.Extended(root) };
        Directory.CreateDirectory(folders[0]);

        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct };
        var level = 0;

        // A level at a time, so every folder's parent is there before it is created.
        for (var depth = 0; depth < shape.Depth; depth++)
        {
            var parents = folders.Count - level;
            var children = new string[parents * shape.Fanout];

            for (var p = 0; p < parents; p++)
            {
                for (var c = 0; c < shape.Fanout; c++)
                {
                    children[(p * shape.Fanout) + c] = Path.Join(folders[level + p], $"d{c}");
                }
            }

            Parallel.ForEach(children, options, child => Directory.CreateDirectory(child));

            level = folders.Count;
            folders.AddRange(children);
        }

        // Dealt across every folder in turn, so a deep tree's files are at every depth rather than
        // only at its deepest.
        Parallel.For(0, folders.Count, options, f =>
        {
            for (long n = f; n < shape.Files; n += folders.Count)
            {
                var file = Path.Join(folders[f], $"f{n}");

                // CreateNew, so a name the shape repeated would fail here rather than leave a tree with
                // fewer files than it claims.
                new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1).Dispose();

                if (shape.ReadOnly)
                {
                    File.SetAttributes(file, FileAttributes.ReadOnly);
                }
            }
        });
    }
}
