using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.VirtualDisks;

namespace Deguffer.Core.Execution;

/// <summary>
/// One registered disk or folder, with what a planning pass found of it.
/// </summary>
/// <param name="Storage">The disk or folder, and whose it is.</param>
/// <param name="RegisteredBy">The <see cref="ICleanupProvider.Id"/> of the row whose records name it.</param>
/// <param name="Presence">
/// What Windows said about it when the pass began. A refusal is not an absence: a disk behind an access
/// rule may be there, so it is protected as one that is.
/// </param>
/// <param name="Form">
/// <see cref="Storage"/>'s path in the form comparisons use: canonical where it can be put in that form,
/// so a record spelled with an 8.3 alias still reads as inside the folder a step names.
/// </param>
public sealed record RegisteredEntry(VirtualMachineStorage Storage, string RegisteredBy, PathPresence Presence, string Form);

/// <summary>
/// Every virtual machine's disk and folder the rows registered for one planning pass, read before any
/// row plans, so every plan in the pass is held to the same set. See
/// <see cref="IRegistersVirtualMachineStorage"/>.
///
/// <para>The reading, kept apart from <see cref="VirtualMachineStoragePlan"/>, which is the rule (G2).</para>
/// </summary>
public sealed record RegisteredStorage(IReadOnlyList<RegisteredEntry> Entries)
{
    public static readonly RegisteredStorage None = new([]);

    /// <summary>
    /// Asks every row in <paramref name="providers"/> that registers storage, and probes each path once.
    /// </summary>
    public static async Task<RegisteredStorage> ReadAsync(IEnumerable<ICleanupProvider> providers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providers);

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
                    ComparedForm(path)));
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
    /// <paramref name="path"/> in the form an entry's <see cref="RegisteredEntry.Form"/> is in, for a
    /// comparison against it.
    /// </summary>
    public static string ComparedForm(string path)
    {
        var display = LongPath.Display(path);

        return LongPath.Canonical(display) ?? display;
    }
}
