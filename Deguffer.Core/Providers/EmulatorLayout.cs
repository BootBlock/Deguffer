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
    /// The folders in <paramref name="root"/> holding a folder for each game, where this layout takes a
    /// cache from inside each game's folder. None for an emulator that keeps one cache folder for every
    /// game.
    /// </summary>
    public virtual IReadOnlyList<EmulatorGameCaches> GameCachesIn(string root) => [];

    /// <summary>
    /// Whether this layout takes a declared folder as its own only where its program is there, because
    /// its settings file is one another emulator writes too. A folder such a layout proved is not also
    /// read as the folder of one that proved it by that settings file alone: Vita3K's
    /// <c>config.yml</c> beside its program is the file an older RPCS3 wrote.
    /// </summary>
    public virtual bool IsKnownByItsProgram => false;

    /// <summary>
    /// Whether nothing proves <paramref name="root"/> to be this emulator's but a settings file
    /// <paramref name="other"/> writes there too. A marker of this layout's own that Windows would not
    /// answer for counts as present, so the folder stays this emulator's as well.
    /// </summary>
    public bool IsProvenOnlyByMarkersOf(EmulatorLayout other, string root) =>
        Markers
            .Where(marker => !other.Markers.Contains(marker, StringComparer.OrdinalIgnoreCase))
            .All(marker => LongPath.ProbeFile(Path.Combine(root, marker)) is PathPresence.Absent);

    /// <summary>
    /// Whether one of this emulator's programs is in <paramref name="folder"/>. A refusal counts, so the
    /// root is probed and reported as unread rather than the folder said to hold no emulator.
    /// </summary>
    protected bool HoldsProgram(string folder) =>
        ProcessNames.Any(program => LongPath.ProbeFile(Path.Combine(folder, program + ".exe")) is not PathPresence.Absent);

    /// <summary>
    /// The folder called <paramref name="name"/> beside this emulator's program, where
    /// <paramref name="folder"/> is the program's folder or that folder itself.
    /// </summary>
    protected IEnumerable<string> FolderBesideProgram(string folder, string name)
    {
        if (HoldsProgram(folder))
        {
            yield return Path.Combine(folder, name);
        }

        if (Path.GetFileName(folder).Equals(name, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(folder) is { } parent
            && HoldsProgram(parent))
        {
            yield return folder;
        }
    }

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
