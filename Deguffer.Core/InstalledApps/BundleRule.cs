using Deguffer.Core.Safety;

namespace Deguffer.Core.InstalledApps;

/// <summary>
/// Judges a WiX Burn bundle by the Windows Installer packages it installed (§7.3).
///
/// <para>A bundle's uninstaller is a copy of its setup in the package cache, and a cleaner that
/// empties that cache leaves the uninstaller gone while every package the bundle installed is still
/// there, often hidden from the list because the bundle stood for them. Removing that entry hides
/// the programs for good. So a bundle is gone only when every package that names it is a product
/// Windows Installer no longer knows.</para>
///
/// <para>Each package's provider names the bundle under <c>Dependents</c> by its <c>Uninstall</c>
/// key name, and its default value is the package's product code, a patch code, or nothing. Only a
/// product code can be proved gone: a patch code is unknown to <c>MsiQueryProductState</c> whether
/// or not the patch is there, a missing code names nothing to ask about, and a code that names
/// another bundle names something Windows Installer never knew.</para>
/// </summary>
internal static class BundleRule
{
    /// <summary>Values Burn writes into a bundle's entry, any one of which marks it as a bundle.</summary>
    private static readonly string[] BundleValues =
        ["BundleProviderKey", "BundleCachePath", "BundleUpgradeCode", "BundleVersion", "EngineVersion"];

    /// <summary>
    /// Whether the entry is a bundle's: it carries a value only Burn writes, or a package names it
    /// as its dependent, which is all a shell Burn left behind may still show.
    /// </summary>
    public static bool IsBundle(UninstallRecord record, PackageDependencies dependencies) =>
        BundleValues.Any(record.Values.IsSet) || PackagesOf(record, dependencies).Count > 0;

    public static Gone Judge(UninstallRecord record, StaleEvidence evidence)
    {
        var dependencies = evidence.Dependencies.Read();

        if (dependencies.Presence is not PathPresence.Present)
        {
            return Gone.Unproven(
                "Windows would not show every package registration, so Deguffer cannot tell what this bundle installed.");
        }

        var packages = PackagesOf(record, dependencies);

        if (packages.Count == 0)
        {
            return Gone.Unproven(
                "No package registration names this bundle, so Deguffer cannot tell what it installed.");
        }

        var codes = new List<Guid>(packages.Count);

        foreach (var package in packages)
        {
            if (!Guid.TryParseExact(package.Code, "B", out var code))
            {
                return Gone.Unproven(
                    $"The package registration {package.Key} names no product code, so Deguffer cannot tell whether what this bundle installed is still there.");
            }

            if (!codes.Contains(code))
            {
                codes.Add(code);
            }
        }

        var answers = codes.Select(code => Ask(code, evidence)).ToList();

        return answers.FirstOrDefault(a => a.Verdict?.Standing is EntryStanding.Installed) is { Verdict: not null } installed ? installed
            : answers.FirstOrDefault(a => a.Verdict is not null) is { Verdict: not null } unproven ? unproven
            : Gone.Proven(codes.Count == 1
                ? "Windows Installer does not know the product this bundle installed"
                : $"Windows Installer knows none of the {codes.Count} products this bundle installed");
    }

    private static Gone Ask(Guid code, StaleEvidence evidence)
    {
        var patches = evidence.Installer.QueryPatches();
        var named = code.ToString("B").ToUpperInvariant();

        if (patches.Codes.Contains(code))
        {
            return Gone.Installed($"Windows Installer still holds the patch {named} this bundle installed.");
        }

        switch (evidence.Installer.QueryProductState(code))
        {
            case InstallerProductState.Installed or InstallerProductState.Advertised:
                return Gone.Installed($"Windows Installer reports the product {named} this bundle installed as still there.");

            case InstallerProductState.OtherAccount:
                return Gone.Installed($"Windows Installer reports the product {named} this bundle installed as installed for another account.");

            case InstallerProductState.Unknown:
                break;

            default:
                return Gone.Unproven(
                    $"Windows Installer did not answer for the product {named} this bundle installed, so Deguffer cannot tell whether it is still there.");
        }

        if (!patches.IsComplete)
        {
            return Gone.Unproven(
                $"Windows Installer would not list every patch, so Deguffer cannot tell whether {named} is a patch still there.");
        }

        return evidence.NonInstallerEntryNamed(code) switch
        {
            PathPresence.Absent => Gone.Proven(string.Empty),
            PathPresence.Present => Gone.Unproven(
                $"The package registration names {named}, which is another entry rather than a product, so Deguffer cannot tell whether it is still there."),
            _ => Gone.Unproven(
                $"Windows would not say whether an entry is named {named}, so Deguffer cannot tell whether it is a product."),
        };
    }

    /// <summary>
    /// The providers that name the bundle, less its own: a bundle registers a provider of its own,
    /// which names the bundle rather than a package.
    /// </summary>
    private static IReadOnlyList<PackageProvider> PackagesOf(UninstallRecord record, PackageDependencies dependencies)
    {
        var own = record.Values.Text("BundleProviderKey");

        return [.. dependencies.ProvidersOf(record.Key.Name).Where(provider =>
            !string.Equals(provider.Key, own, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(provider.Code, record.Key.Name, StringComparison.OrdinalIgnoreCase))];
    }
}
