using Deguffer.Core.Safety;

namespace Deguffer.Core.InstalledApps;

/// <summary>
/// What was asked during one reading, so nothing is asked twice (G4): hundreds of entries share a
/// few executables, folders and package registrations.
/// </summary>
internal sealed class ReadingAnswers(
    IUninstallRegistry registry,
    IWindowsInstaller installer,
    IPathProbe probe,
    IPackageDependencies dependencies) : IWindowsInstaller, IPathProbe, IPackageDependencies
{
    private readonly Dictionary<Guid, InstallerProductState> _products = [];
    private readonly Dictionary<Guid, PathPresence> _entriesNamed = [];
    private readonly Dictionary<string, PathPresence> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PathPresence> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (PathPresence Presence, bool? IsLink)> _directories = new(StringComparer.OrdinalIgnoreCase);
    private InstallerPatches? _patches;
    private PackageDependencies? _dependencies;

    public InstallerProductState QueryProductState(Guid productCode) =>
        Remembered(_products, productCode, installer.QueryProductState);

    public InstallerPatches QueryPatches() => _patches ??= installer.QueryPatches();

    public PackageDependencies Read() => _dependencies ??= dependencies.Read();

    public PathPresence ProbeFile(string path) => Remembered(_files, path, probe.ProbeFile);

    public PathPresence ProbeEntry(string path) => Remembered(_entries, path, probe.ProbeEntry);

    public PathPresence ProbeDirectory(string path, out bool? isLink)
    {
        var answer = Remembered(_directories, path, p => (Presence: probe.ProbeDirectory(p, out var link), IsLink: link));
        isLink = answer.IsLink;
        return answer.Presence;
    }

    /// <summary>See <see cref="StaleEvidence.NonInstallerEntryNamed"/>.</summary>
    public PathPresence NonInstallerEntryNamed(Guid code) => Remembered(_entriesNamed, code, AskEveryScope);

    private PathPresence AskEveryScope(Guid code)
    {
        var name = code.ToString("B");
        var answer = PathPresence.Absent;

        foreach (var scope in UninstallScopes.All)
        {
            var (presence, values) = registry.ReadOne(new UninstallKey(scope, name));

            switch (presence)
            {
                case PathPresence.Present when !StaleRule.IsInstallerEntry(name, values, out _):
                    return PathPresence.Present;

                case PathPresence.Refused:
                    answer = PathPresence.Refused;
                    break;
            }
        }

        return answer;
    }

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
