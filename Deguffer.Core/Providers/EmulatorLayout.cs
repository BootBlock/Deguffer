using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where one console emulator keeps its data on Windows, how to prove a folder is that data, and
/// which paths inside it are a shader cache it compiles again.
///
/// <para><b>Recognised paths from a proven root, never names found anywhere.</b> Dolphin, DuckStation
/// and PPSSPP each keep a folder of the user's own post-processing shaders beside the compiled cache,
/// and in Dolphin the two are <c>Shaders</c> and <c>Cache\Shaders</c>. A rule that matched the name
/// would delete the user's shaders. So every target is a child of a folder this layout names relative
/// to a root, and a root counts only where the file the emulator itself writes there is present:
/// an arbitrary folder holding a <c>cache</c> directory is not an emulator.</para>
///
/// <para><b>An unrecognised child here is somebody's save file.</b> Every emulator keeps its cache
/// beside memory cards, save states, firmware and installed titles, and no emulator offers an
/// eviction command Deguffer could call instead (§5.1). Everything a layout does not name is left
/// alone, at every level between the root and the cache.</para>
/// </summary>
public abstract class EmulatorLayout
{
    /// <summary>The emulator's name, as the user knows it.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// The emulator's processes, without extension. While one runs it may be writing the cache, so
    /// its targets are held back (§5.3).
    /// </summary>
    public abstract IReadOnlyList<string> ProcessNames { get; }

    /// <summary>
    /// Files relative to a root, any one of which proves the folder is this emulator's. Each is a
    /// file the emulator writes itself, never a directory a user could make by hand.
    /// </summary>
    protected abstract IReadOnlyList<string> Markers { get; }

    /// <summary>
    /// Where the emulator puts its data when nobody has told it otherwise: a known folder, a registry
    /// value it writes itself, or an environment variable it reads. A candidate here is only a place
    /// to look, and <see cref="Proof"/> decides.
    /// </summary>
    public abstract IEnumerable<string> FixedRoots(IUserEnvironment environment);

    /// <summary>
    /// Where the emulator puts its data when it runs from <paramref name="folder"/>, a folder the user
    /// declared. The folder itself is always a candidate too, so a user who picks the data folder
    /// rather than the program's folder is still understood.
    /// </summary>
    public abstract IEnumerable<string> RootsDeclaredBy(string folder);

    /// <summary>
    /// What §5.6 must assert survived in a root, as name and reason. Named because most of them are
    /// never enumerated: the plan reaches the cache by name and walks nothing else.
    /// </summary>
    public abstract IReadOnlyList<(string Name, string Reason)> ProtectedNames { get; }

    /// <summary>The folders in <paramref name="root"/> this layout takes a cache from.</summary>
    public abstract IReadOnlyList<EmulatorCacheFolder> CacheFoldersIn(string root);

    /// <summary>
    /// Whether <paramref name="root"/> holds a marker. Refused where no marker is there and Windows
    /// would not answer for one, so an unreadable root is reported rather than read as no emulator.
    /// </summary>
    public PathPresence Proof(string root)
    {
        var refused = false;

        foreach (var marker in Markers)
        {
            switch (LongPath.ProbeFile(Path.Combine(root, marker)))
            {
                case PathPresence.Present:
                    return PathPresence.Present;

                case PathPresence.Refused:
                    refused = true;
                    break;
            }
        }

        return refused ? PathPresence.Refused : PathPresence.Absent;
    }
}
