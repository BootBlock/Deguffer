namespace Deguffer.Core.Providers;

/// <summary>
/// A provider whose own row offers an entry that sits directly inside a temporary folder.
///
/// <para><b>It exists so that one entry is offered on one row.</b> <see cref="TempDirectoryProvider"/>
/// empties each temporary folder of whatever is old enough, so without this every entry another
/// provider names by the tool that wrote it would be counted twice: once on that tool's row, and
/// again on "Temporary files". The tool's row is the one that knows what the entry is, so the
/// temporary-folder row leaves the entry to it, however that row is ticked.</para>
///
/// <para>A tenant answers about entries it recognises, not about entries it would take today. An
/// entry a running program is using, or one too recent to offer, is still the tenant's, and the
/// tenant's plan is what says so and protects it. Handing it back to the temporary-folder row would
/// offer it there under a weaker rule than the one that recognised it.</para>
/// </summary>
public interface ITemporaryFolderTenant
{
    /// <summary>The row's name, which the temporary-folder row quotes for what it leaves out.</summary>
    string Name { get; }

    /// <summary>
    /// The full paths of the entries directly inside <paramref name="folders"/> that this provider's
    /// own row offers, or would offer once it is safe to. Empty where it offers none, which includes
    /// a provider that is not present on this machine.
    /// </summary>
    /// <param name="folders">This machine's temporary folders, as <see cref="TempRoots"/> resolves them.</param>
    Task<IReadOnlyList<string>> ClaimedEntriesAsync(IReadOnlyList<string> folders, CancellationToken ct = default);

    /// <summary>
    /// Drops what the claim was worked out from, so the next one looks at the folder again. The
    /// temporary-folder row calls it whenever it is itself planned again. See
    /// <see cref="ICleanupProvider.InvalidateCaches"/>.
    /// </summary>
    void InvalidateCaches();
}
