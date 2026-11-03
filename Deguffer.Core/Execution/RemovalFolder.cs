namespace Deguffer.Core.Execution;

/// <summary>
/// A folder of a removal: what the walk found in it, and how much of it is still to be tried before
/// the folder itself can be. See <see cref="TreeRemoval"/>.
///
/// <para><b>Counted, so a folder is tried the moment it can be and never before.</b> Its own listing,
/// each file and link in it handed to a delete, and each folder found in it are outstanding until
/// they are done. Whoever finishes the last of them tries the folder, so no folder is tried while
/// anything inside it is still to be tried, and none waits for the rest of the tree.</para>
/// </summary>
/// <param name="foundAs">The path the folder was found at, in extended form.</param>
/// <param name="parent">The folder it was found in, or null for the removal's root.</param>
internal sealed class RemovalFolder(string foundAs, RemovalFolder? parent) : RemovalWalk.Folder(foundAs)
{
    // One for its listing to begin with, which a folder found in it can only add to: its listing
    // finds the folder, and so is still outstanding when the folder is counted.
    private int _outstanding = 1;

    // Written by the one thread that tries the folder, and read once every thread of the removal has
    // ended.
    private bool _tried;
    private DirectoryDeletion.Standing? _left;

    /// <summary>The folder it was found in, or null for the removal's root.</summary>
    public RemovalFolder? Parent => parent;

    /// <summary>Whether a deletion of the folder itself was attempted.</summary>
    public bool WasTried => _tried;

    /// <summary>Why the folder is still there, where its deletion was attempted and refused.</summary>
    public DirectoryDeletion.Standing? Left => _left;

    /// <summary>Count <paramref name="entries"/> more of its entries as handed to a delete.</summary>
    public void Expect(int entries) => Interlocked.Add(ref _outstanding, entries);

    /// <summary>
    /// Count one outstanding thing done: its listing, one of its entries, or a folder inside it. True
    /// for the call that finishes the last, which is then the one to try the folder.
    /// </summary>
    public bool Finish() => Interlocked.Decrement(ref _outstanding) == 0;

    /// <summary>Record what the attempt to delete the folder itself left: null where it went.</summary>
    public void Record(DirectoryDeletion.Standing? left)
    {
        _left = left;
        _tried = true;
    }

    protected override RemovalWalk.Folder NewChild(string path)
    {
        Interlocked.Increment(ref _outstanding);
        return new RemovalFolder(path, this);
    }
}
