using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.VirtualDisks;

namespace Deguffer.Core.Execution;

/// <summary>
/// One registered disk or folder, with what Windows said of it when it was read.
/// </summary>
/// <param name="Storage">The disk or folder, and whose it is.</param>
/// <param name="RegisteredBy">The <see cref="ICleanupProvider.Id"/> of the row whose records name it.</param>
/// <param name="Presence">
/// What Windows said about it when it was read. A refusal is not an absence: a disk behind an access
/// rule may be there, so it is protected as one that is.
/// </param>
/// <param name="Reached">
/// Where it is, as <see cref="RegisteredStorage.Reach"/> gives it, for the comparison with what a step
/// reaches.
/// </param>
public sealed record RegisteredEntry(VirtualMachineStorage Storage, string RegisteredBy, PathPresence Presence, ReachedFolder Reached);

/// <summary>
/// Every virtual machine's disk and folder the rows registered, read once before a planning pass so every
/// plan in it is held to the same set, and again before a clean so the clean proves them. See
/// <see cref="IRegistersVirtualMachineStorage"/>.
///
/// <para>The reading, kept apart from <see cref="VirtualMachineStoragePlan"/>, which is the rule (G2).</para>
/// </summary>
public sealed record RegisteredStorage(IReadOnlyList<RegisteredEntry> Entries)
{
    public static readonly RegisteredStorage None = new([]);

    /// <summary>
    /// Asks every row in <paramref name="providers"/> that registers storage, and probes and places each
    /// path once.
    /// </summary>
    public static async Task<RegisteredStorage> ReadAsync(
        IEnumerable<ICleanupProvider> providers, IVolumeInventory volumes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(volumes);

        List<RegisteredEntry> entries = [];

        foreach (var provider in providers)
        {
            if (provider is not IRegistersVirtualMachineStorage registrar)
            {
                continue;
            }

            foreach (var storage in await registrar.RegisteredStorageAsync(ct).ConfigureAwait(false))
            {
                var path = LongPath.Display(storage.Path);

                entries.Add(new RegisteredEntry(
                    storage with { Path = path },
                    provider.Id,
                    LongPath.ProbeEntry(path),
                    Reach(path, volumes)));
            }
        }

        return entries.Count == 0 ? None : new RegisteredStorage(entries);
    }

    /// <summary>
    /// The entries a step could destroy: every one Windows did not report absent. A disk that is not
    /// there cannot be deleted, and withholding a step for a stale record would hold a folder back for
    /// ever over nothing.
    /// </summary>
    public IEnumerable<RegisteredEntry> Standing => Entries.Where(entry => entry.Presence != PathPresence.Absent);

    /// <summary>
    /// The folder <paramref name="path"/> names, at every path it is reachable at, for a comparison that
    /// must not fail in the direction that deletes.
    ///
    /// <para><b>An alias Windows will not expand is not ruled out.</b> A record or a step may spell a
    /// folder with an 8.3 alias, and <see cref="LongPath.Canonical"/> answers null where Windows would not
    /// describe it: "cannot say", never a guess. Compared as spelled, a disk recorded through such an
    /// alias would read as outside the folder a step names by its long name, and the step would run. So
    /// the path is cut back to the longest part Windows can name, and everything below that part is
    /// treated as possibly the disk's: a step there is withheld, and a step holding it is too.</para>
    ///
    /// <para><see cref="ReachedFolder"/> then follows that part to every path it is reachable at, so a
    /// step naming the folder through a <c>subst</c> letter or another mount of its volume still meets
    /// it.</para>
    /// </summary>
    public static ReachedFolder Reach(string path, IVolumeInventory volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        var known = LongPath.Display(path);

        while (LongPath.Canonical(known) is null && Path.GetDirectoryName(known) is { Length: > 0 } above)
        {
            known = above;
        }

        return ReachedFolder.At(known, volumes);
    }
}
