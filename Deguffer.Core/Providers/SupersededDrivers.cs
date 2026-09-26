using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>An older driver package, and the newest of its kind beside it.</summary>
/// <param name="Package">The older one. Its folder is always a child of the store.</param>
/// <param name="Newest">The newest package of the same kind, which stays.</param>
public sealed record SupersededDriver(DriverPackage Package, DriverPackage Newest);

/// <summary>
/// Which driver packages in the store have a newer version of themselves beside it.
///
/// <para><b>Deguffer's own reading, and a heuristic.</b> Packages are of one kind where the INF's own
/// name, its provider, its device class and any extension it provides all match, and the newest by
/// its stated date, then version, is kept. Two different packages could share all four, which is why
/// <see cref="DriverStoreProvider"/> has Windows' own cleanup decide what goes wherever it can, and
/// uses this for the figure and for what §5.6 checks afterwards.</para>
///
/// <para><b>Every doubt keeps a package.</b> Several packages sharing the newest date and version are
/// all newest. A package a device is installed with is never offered, whatever its age: Windows
/// refuses to remove one, and <c>/force</c>, which overrides that, is never passed. And a package whose
/// folder Windows would not name, or named outside the store, is never offered, because §5.2 allows
/// only a child the store recognises.</para>
/// </summary>
/// <param name="Superseded">The packages offered, each a child of the store.</param>
/// <param name="Newest">The newest of every kind that has an older version.</param>
/// <param name="InUse">Older packages left alone because a device is installed with them.</param>
/// <param name="Unplaced">Older packages left alone because Windows did not name a folder inside the store for them.</param>
public sealed record SupersededDrivers(
    IReadOnlyList<SupersededDriver> Superseded,
    IReadOnlyList<DriverPackage> Newest,
    IReadOnlyList<DriverPackage> InUse,
    int Unplaced)
{
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    /// <param name="packages">Every third-party package the store lists.</param>
    /// <param name="repository">The store's <c>FileRepository</c>, whose children the packages are.</param>
    public static SupersededDrivers Of(IReadOnlyList<DriverPackage> packages, string repository)
    {
        ArgumentNullException.ThrowIfNull(packages);

        var superseded = new List<SupersededDriver>();
        var newestOfEach = new List<DriverPackage>();
        var inUse = new List<DriverPackage>();
        var unplaced = 0;

        var kinds = packages.GroupBy(
            p => (p.OriginalName, p.Provider, p.ClassGuid, p.ExtensionId ?? string.Empty),
            KindComparer.Instance);

        foreach (var kind in kinds)
        {
            var newest = kind.Max(Stamp);
            var older = kind.Where(p => Stamp(p).CompareTo(newest) < 0).ToList();

            if (older.Count == 0)
            {
                continue;
            }

            var current = kind.First(p => Stamp(p).CompareTo(newest) == 0);
            newestOfEach.AddRange(kind.Where(p => Stamp(p).CompareTo(newest) == 0));

            foreach (var package in older)
            {
                if (package.InUse)
                {
                    inUse.Add(package);
                }
                else if (IsChildOf(package.Folder, repository))
                {
                    superseded.Add(new SupersededDriver(package, current));
                }
                else
                {
                    unplaced++;
                }
            }
        }

        return new SupersededDrivers(
            [.. superseded.OrderBy(s => s.Package.Folder, Names)],
            [.. newestOfEach.OrderBy(p => p.PublishedName, Names)],
            [.. inUse.OrderBy(p => p.PublishedName, Names)],
            unplaced);
    }

    private static (DateOnly, Version) Stamp(DriverPackage package) => (package.Date, package.Version);

    /// <summary>
    /// Whether <paramref name="folder"/> is one level inside <paramref name="repository"/> and not the
    /// repository itself: the only shape a package's folder has.
    /// </summary>
    public static bool IsChildOf(string? folder, string repository) =>
        folder is { Length: > 0 }
        && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(LongPath.Display(folder))) is { } parent
        && Names.Equals(parent, Path.TrimEndingDirectorySeparator(LongPath.Display(repository)));

    /// <summary>Kinds compared as Windows compares these names: without regard to case.</summary>
    private sealed class KindComparer : IEqualityComparer<(string, string, string, string)>
    {
        public static readonly KindComparer Instance = new();

        public bool Equals((string, string, string, string) x, (string, string, string, string) y) =>
            Names.Equals(x.Item1, y.Item1) && Names.Equals(x.Item2, y.Item2)
            && Names.Equals(x.Item3, y.Item3) && Names.Equals(x.Item4, y.Item4);

        public int GetHashCode((string, string, string, string) kind) => HashCode.Combine(
            Names.GetHashCode(kind.Item1), Names.GetHashCode(kind.Item2),
            Names.GetHashCode(kind.Item3), Names.GetHashCode(kind.Item4));
    }
}
