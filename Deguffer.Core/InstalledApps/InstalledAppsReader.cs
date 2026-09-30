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
public sealed class InstalledAppsReader(IUninstallRegistry registry, IWindowsInstaller installer)
{
    public static InstalledAppsReader Default { get; } =
        new(WindowsUninstallRegistry.Default, WindowsInstaller.Default);

    /// <summary>
    /// Every entry. Windows Installer and the filesystem are asked once per product code and per
    /// path for the life of the reading (G4), since hundreds of entries share a few executables.
    /// </summary>
    public InstalledAppsReading Read(CancellationToken ct)
    {
        var answers = new Answers(installer);
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
                entries.Add(Evaluate(record, answers));
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
            : Evaluate(new UninstallRecord(key, values, IsReadable: presence is PathPresence.Present), new Answers(installer));
    }

    private static InstalledEntry Evaluate(UninstallRecord record, Answers answers)
    {
        var values = record.Values;
        var command = UninstallCommand.Parse(values.Text("UninstallString"), answers.ProbeFile);
        var standing = StaleRule.Decide(record, command, answers.AskInstaller, answers.ProbeDirectory);
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

    /// <summary>What was asked during one reading, so nothing is asked twice.</summary>
    private sealed class Answers(IWindowsInstaller installer)
    {
        private readonly Dictionary<Guid, InstallerProductState> _products = [];
        private readonly Dictionary<string, PathPresence> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PathPresence> _directories = new(StringComparer.OrdinalIgnoreCase);

        public InstallerProductState AskInstaller(Guid code) =>
            Remembered(_products, code, installer.QueryProductState);

        public PathPresence ProbeFile(string path) => Remembered(_files, path, LongPath.ProbeFile);

        public PathPresence ProbeDirectory(string path) => Remembered(_directories, path, LongPath.ProbeDirectory);

        private static TValue Remembered<TKey, TValue>(Dictionary<TKey, TValue> cache, TKey key, Func<TKey, TValue> ask)
            where TKey : notnull
        {
            if (!cache.TryGetValue(key, out var answer))
            {
                answer = ask(key);
                cache[key] = answer;
            }

            return answer;
        }
    }
}
