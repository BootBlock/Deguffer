using Deguffer.Core.Safety;

namespace Deguffer.Core.InstalledApps;

/// <summary>Every entry, and the scopes whose key could not be read.</summary>
/// <param name="RefusedScopes">
/// Keys Windows would not open. Reported, because an empty list for a key nobody could read is not
/// a machine with nothing installed there.
/// </param>
public sealed record InstalledAppsReading(IReadOnlyList<InstalledEntry> Entries, IReadOnlyList<UninstallScope> RefusedScopes);

/// <summary>
/// Reads the three <c>Uninstall</c> keys and decides each entry's list (§7.3).
///
/// <para>Orchestration only: <see cref="EntryListing"/> decides what Windows lists,
/// <see cref="UninstallCommand"/> what an entry runs, and <see cref="StaleRule"/> which list it is
/// in.</para>
/// </summary>
public sealed class InstalledAppsReader(
    IUninstallRegistry registry,
    IWindowsInstaller installer,
    IPathProbe probe,
    ISystemDirectories system,
    IPackageDependencies dependencies,
    IVolumeInventory volumes)
{
    public static InstalledAppsReader Default { get; } = new(
        WindowsUninstallRegistry.Default,
        WindowsInstaller.Default,
        LongPathProbe.Default,
        SystemDirectories.Current,
        WindowsPackageDependencies.Default,
        VolumeInventory.Current);

    /// <summary>
    /// Every entry. Each question is asked once for the life of the reading (G4). See
    /// <see cref="ReadingAnswers"/>.
    /// </summary>
    public InstalledAppsReading Read(CancellationToken ct)
    {
        var evidence = NewEvidence();
        var entries = new List<InstalledEntry>();
        var refused = new List<UninstallScope>();

        foreach (var scope in UninstallScopes.All)
        {
            ct.ThrowIfCancellationRequested();

            var read = registry.Read(scope);

            if (read.Presence is PathPresence.Refused)
            {
                refused.Add(scope);
            }

            foreach (var record in read.Records)
            {
                ct.ThrowIfCancellationRequested();
                entries.Add(Evaluate(record, evidence));
            }
        }

        return new InstalledAppsReading(entries, refused);
    }

    /// <summary>
    /// One entry, read and decided again, or null where Windows says its key is gone. Asked
    /// immediately before an action, so the decision it acts on is the machine's as it now stands.
    /// </summary>
    public InstalledEntry? ReadAgain(UninstallKey key)
    {
        var (presence, values) = registry.ReadOne(key);

        return presence is PathPresence.Absent
            ? null
            : Evaluate(new UninstallRecord(key, values, IsReadable: presence is PathPresence.Present), NewEvidence());
    }

    private StaleEvidence NewEvidence()
    {
        var answers = new ReadingAnswers(registry, installer, probe, dependencies);

        return new StaleEvidence(answers, new InstalledPaths(answers, system, volumes), answers, answers.NonInstallerEntryNamed);
    }

    private static InstalledEntry Evaluate(UninstallRecord record, StaleEvidence evidence)
    {
        var values = record.Values;
        var command = UninstallCommand.Parse(values.Text("UninstallString"), evidence.Paths.ProbeFile);
        var standing = StaleRule.Decide(record, command, evidence);
        var visibility = standing.Standing is EntryStanding.OtherAccount
            ? EntryVisibility.OtherAccount
            : EntryListing.Of(record);

        return new InstalledEntry(
            record.Key,
            values.Text("DisplayName") ?? record.Key.Name,
            values.Text("Publisher"),
            values.Text("DisplayVersion"),
            values,
            visibility,
            command,
            standing,
            NoRemove: values.Flag("NoRemove"));
    }
}
