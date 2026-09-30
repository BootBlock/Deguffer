using System.Security;
using Deguffer.Core.Safety;
using Microsoft.Win32;

namespace Deguffer.Core.InstalledApps;

/// <summary>A package provider that lists a bundle as one of its dependents.</summary>
/// <param name="Key">The provider's key name under <c>Dependencies</c>.</param>
/// <param name="Code">
/// The provider's default value where it is a string: a Windows Installer package's product code or
/// patch code. Null where the provider has no default value, as an executable package's has not, or
/// where the value is not a string.
/// </param>
public sealed record PackageProvider(string Key, string? Code);

/// <summary>Every package provider that lists a dependent, by the dependent it lists.</summary>
/// <param name="Presence">
/// <see cref="PathPresence.Refused"/> where Windows would not open a provider, which may be one that
/// lists any bundle. <see cref="PathPresence.Present"/> where every provider was read.
/// </param>
public sealed record PackageDependencies(
    PathPresence Presence,
    IReadOnlyDictionary<string, IReadOnlyList<PackageProvider>> ByDependent)
{
    /// <summary>The providers that list <paramref name="dependent"/>, or none.</summary>
    public IReadOnlyList<PackageProvider> ProvidersOf(string dependent) =>
        ByDependent.TryGetValue(dependent, out var providers) ? providers : [];
}

/// <summary>
/// The WiX dependency registrations a Burn bundle's packages write, behind a seam so the rule that
/// judges a bundle by its packages (§7.3) is proved against registrations a test wrote.
///
/// <para>Each package registers a provider under <c>Software\Classes\Installer\Dependencies</c>,
/// in <c>HKEY_LOCAL_MACHINE</c> for a per-machine package and <c>HKEY_CURRENT_USER</c> for a
/// per-user one, and lists each bundle that installed it under <c>Dependents</c> by the bundle's
/// <c>Uninstall</c> key name.</para>
/// </summary>
public interface IPackageDependencies
{
    /// <summary>Every registration, read once. Never throws for a refusal.</summary>
    PackageDependencies Read();
}

/// <inheritdoc />
/// <remarks>
/// The key is shared between the registry views and readable without elevation: measured on
/// 2026-09-30, 4,212 providers under <c>HKEY_LOCAL_MACHINE</c>, 4,101 of them listing a dependent.
/// </remarks>
public sealed class WindowsPackageDependencies : IPackageDependencies
{
    public const string KeyPath = @"Software\Classes\Installer\Dependencies";

    public static WindowsPackageDependencies Default { get; } = new([
        () => OpenDependencies(RegistryHive.LocalMachine),
        () => OpenDependencies(RegistryHive.CurrentUser)]);

    private readonly IReadOnlyList<Func<RegistryKey?>> _roots;

    /// <param name="roots">
    /// Each opens one <c>Dependencies</c> key, or returns null where it is not there. A test points
    /// these at scratch keys under <c>HKEY_CURRENT_USER</c>.
    /// </param>
    internal WindowsPackageDependencies(IReadOnlyList<Func<RegistryKey?>> roots) => _roots = roots;

    public PackageDependencies Read()
    {
        var byDependent = new Dictionary<string, List<PackageProvider>>(StringComparer.OrdinalIgnoreCase);
        var refused = false;

        foreach (var open in _roots)
        {
            refused |= !ReadRoot(open, byDependent);
        }

        return new PackageDependencies(
            refused ? PathPresence.Refused : PathPresence.Present,
            byDependent.ToDictionary(
                pair => pair.Key,
                IReadOnlyList<PackageProvider> (pair) => pair.Value,
                StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Every provider under one root, or false where Windows refused any part of it.</summary>
    private static bool ReadRoot(Func<RegistryKey?> open, Dictionary<string, List<PackageProvider>> byDependent)
    {
        try
        {
            using var root = open();

            if (root is null)
            {
                return true;
            }

            var complete = true;

            foreach (var name in root.GetSubKeyNames())
            {
                complete &= ReadProvider(root, name, byDependent);
            }

            return complete;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool ReadProvider(RegistryKey root, string name, Dictionary<string, List<PackageProvider>> byDependent)
    {
        try
        {
            // A provider removed between the listing and the read lists nothing.
            using var provider = root.OpenSubKey(name);

            if (provider is null)
            {
                return true;
            }

            using var dependents = provider.OpenSubKey("Dependents");

            if (dependents is null)
            {
                return true;
            }

            var entry = new PackageProvider(name, provider.GetValue(null) as string);

            foreach (var dependent in dependents.GetSubKeyNames())
            {
                if (!byDependent.TryGetValue(dependent, out var providers))
                {
                    byDependent[dependent] = providers = [];
                }

                providers.Add(entry);
            }

            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static RegistryKey? OpenDependencies(RegistryHive hive)
    {
        // A predefined key: disposing it closes nothing, so the subkey outlives it safely.
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

        return root.OpenSubKey(KeyPath);
    }
}
