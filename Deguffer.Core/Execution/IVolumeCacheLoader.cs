namespace Deguffer.Core.Execution;

/// <summary>
/// Where <see cref="DiskCleanupHandlers"/> gets a handler from: Windows' registration and COM, or a
/// test's recording stand-in.
///
/// <para>Behind an interface so the host's conversation with a handler can be tested: which calls it
/// makes, in which order, and what it reads from each answer. A survey that measured where it should
/// only have started costs a minute and a half on every elevated preview and shows in no outcome,
/// and the real handler on whatever machine runs the suite is that machine's own.</para>
/// </summary>
internal interface IVolumeCacheLoader
{
    /// <summary>
    /// Whether this process is elevated. Windows' setup handlers answer "nothing to delete" to any
    /// other, whatever is on the disk.
    /// </summary>
    bool IsElevated { get; }

    /// <summary>Whether Windows registers <paramref name="handler"/> with a class this process can load.</summary>
    bool IsRegistered(string handler);

    /// <summary>Creates the handler a registration names, with that registration's key open.</summary>
    /// <param name="failure">
    /// Where it would not load, why: <see cref="DiskCleanupNative.REGDB_E_CLASSNOTREG"/> where the
    /// registration is gone, and otherwise what creating it failed with.
    /// </param>
    /// <returns>The handler, or null where it would not load.</returns>
    LoadedVolumeCache? Load(string handler, out int failure);
}

/// <summary>
/// One handler created for one registration, with the registration's key it is initialised with.
/// Disposing releases both.
/// </summary>
/// <param name="Instance">The handler, which implements <see cref="IEmptyVolumeCache"/> and may implement <see cref="IEmptyVolumeCache2"/>.</param>
/// <param name="Key">The registration's key, open until this is disposed.</param>
internal sealed class LoadedVolumeCache(object instance, IntPtr key, Action release) : IDisposable
{
    public object Instance { get; } = instance;

    public IntPtr Key { get; } = key;

    public void Dispose() => release();
}
