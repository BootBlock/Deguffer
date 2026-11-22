using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Every entry of the temporary folders a tenant's row offers, and the rows that offer them, so
/// <see cref="TempDirectoryProvider"/> leaves each entry to the row that knows what it is
/// (<see cref="ITemporaryFolderTenant"/>).
/// </summary>
/// <param name="Paths">Each entry a tenant claimed, as that tenant named it.</param>
/// <param name="Rows">The name of each row that claimed at least one, in the order the tenants are listed.</param>
internal sealed record TenantClaims(IReadOnlySet<string> Paths, IReadOnlyList<string> Rows)
{
    /// <summary>
    /// What each of <paramref name="tenants"/> claims in <paramref name="folders"/>.
    ///
    /// <para><b>Every claim is asked at once</b>, because the temporary-folder row walks nothing until
    /// all of them answer, and asked one after another they cost the row 1.5 to 2.5 s on a cold pass
    /// where asked together they cost 0.7 to 1.6 s. Most claims are a tenant's survey of the folder,
    /// worked out synchronously before the task they return exists, so awaiting the tasks together
    /// alone would still run those surveys in series on this thread. Each claim therefore starts on a
    /// thread of its own. A claim that awaits a tool, as NuGet's does, gives that thread back at its
    /// first await.</para>
    ///
    /// <para>The answers are read in the tenants' order whatever order they arrive in, so the row's
    /// note names the same rows in the same order from one pass to the next.</para>
    /// </summary>
    public static async Task<TenantClaims> ReadAsync(
        IReadOnlyList<ITemporaryFolderTenant> tenants,
        IReadOnlyList<string> folders,
        CancellationToken ct)
    {
        var asked = new Task<IReadOnlyList<string>>[tenants.Count];

        for (var at = 0; at < tenants.Count; at++)
        {
            var tenant = tenants[at];
            asked[at] = WorkerThreads.Shared.RunAsync(() => tenant.ClaimedEntriesAsync(folders, ct), ct).Unwrap();
        }

        // Every answer before any is read, so a failure leaves no claim still running unobserved.
        await Task.WhenAll(asked).ConfigureAwait(false);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<string>();

        for (var at = 0; at < tenants.Count; at++)
        {
            var claimed = await asked[at].ConfigureAwait(false);

            if (claimed.Count > 0)
            {
                paths.UnionWith(claimed);
                rows.Add(tenants[at].Name);
            }
        }

        return new TenantClaims(paths, rows);
    }

    public bool Contains(string path) => Paths.Contains(Path.TrimEndingDirectorySeparator(path));
}
