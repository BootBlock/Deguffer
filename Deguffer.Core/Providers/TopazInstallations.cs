using System.Text.RegularExpressions;
using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What Deguffer could establish about whether a Topaz Labs product is installed.</summary>
public enum TopazInstallStanding
{
    /// <summary>
    /// No installation was found anywhere Deguffer looked, and every place it looked answered. Topaz
    /// renamed its products and discontinued its classic ones, so this is the folder an uninstall or
    /// a rename left behind.
    /// </summary>
    NotInstalled,

    /// <summary>An installation was found, or a Topaz model variable names something in the folder.</summary>
    Installed,

    /// <summary>
    /// Nothing was found, but something Deguffer had to read refused. Treated as installed, which is
    /// the direction that keeps a product's sign-in record.
    /// </summary>
    Unknown,
}

/// <summary>
/// Whether each Topaz Labs product whose folder is in <c>%PROGRAMDATA%\Topaz Labs LLC</c> is still
/// installed, read once for a planning pass.
///
/// <para><b>Three kinds of evidence, any one of which counts as installed.</b> Topaz installs into
/// <c>Program Files\Topaz Labs LLC\&lt;Product&gt;</c>, under the same name as its data folder. A user
/// can choose another folder in the installer, so the <c>Uninstall</c> keys are read as well, by the
/// entry's install folder and by its display name, which Topaz writes as the product's name and its
/// version. And Topaz Video AI's command-line tools read <c>TVAI_MODEL_DIR</c> and
/// <c>TVAI_MODEL_DATA_DIR</c> (<c>VEAI_</c> before 3.1), and Photo AI was reported reading
/// <c>PAI_MODEL_DIR</c>, so a folder one of them names is in use whatever is installed.</para>
///
/// <para><b>"Not installed" is a claim, so a refusal never makes it.</b> A folder Windows would not
/// describe, an <c>Uninstall</c> key it would not open, or an entry it would not read could each be the
/// installation, and the answer is then <see cref="TopazInstallStanding.Unknown"/>.</para>
/// </summary>
public sealed partial class TopazInstallations
{
    /// <summary>The variables Topaz's tools read for where their models are.</summary>
    public static readonly IReadOnlyList<string> ModelVariables =
        ["TVAI_MODEL_DIR", "TVAI_MODEL_DATA_DIR", "VEAI_MODEL_DIR", "VEAI_MODEL_DATA_DIR", "PAI_MODEL_DIR"];

    private readonly IReadOnlyList<string> _variableFolders;
    private readonly IReadOnlyList<UninstallValues> _entries;
    private readonly bool _registryIncomplete;

    private TopazInstallations(
        IReadOnlyList<string> vendorFolders,
        IReadOnlyList<string> variableFolders,
        IReadOnlyList<UninstallValues> entries,
        bool registryIncomplete)
    {
        VendorFolders = vendorFolders;
        _variableFolders = variableFolders;
        _entries = entries;
        _registryIncomplete = registryIncomplete;
    }

    /// <summary>Read the <c>Uninstall</c> keys and the model variables once.</summary>
    public static TopazInstallations Read(ISystemDirectories system, IUninstallRegistry registry, IUserEnvironment environment)
    {
        List<UninstallValues> entries = [];
        var incomplete = false;

        foreach (var scope in UninstallScopes.All)
        {
            var read = registry.Read(scope);
            incomplete |= read.Presence is PathPresence.Refused;

            foreach (var record in read.Records)
            {
                incomplete |= !record.IsReadable;
                entries.Add(record.Values);
            }
        }

        return new TopazInstallations(
            [
                .. new[] { system.ProgramFiles, system.ProgramFilesX86 }
                    .Where(Path.IsPathFullyQualified)
                    .Select(folder => Path.Combine(folder, TopazLayout.VendorFolderName)),
            ],
            [
                .. ModelVariables
                    .Select(environment.GetEnvironmentVariable)
                    .Select(value => LongPath.Configured(value?.Trim().Trim('"')))
                    .OfType<string>(),
            ],
            entries,
            incomplete);
    }

    /// <summary>
    /// <c>Topaz Labs LLC</c> in each program directory, where Topaz installs every product, in
    /// display form. Present or not.
    /// </summary>
    public IReadOnlyList<string> VendorFolders { get; }

    /// <summary>
    /// Every folder a Topaz program may run from: <see cref="VendorFolders"/>, and the install folder of
    /// each <c>Uninstall</c> entry for a product installed somewhere else.
    /// </summary>
    public IReadOnlyList<string> ProgramFolders =>
    [
        .. VendorFolders,
        .. _entries
            .Select(InstallLocation.Of)
            .Where(location => location.Kind is InstallLocationKind.Path
                && TopazLayout.IsProductName(Path.GetFileName(location.Text))
                && !VendorFolders.Any(vendor => LongPath.Contains(vendor, location.Text)))
            .Select(location => location.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>What is known about the product whose data folder is <paramref name="productFolder"/>.</summary>
    public TopazInstallStanding Of(string productFolder)
    {
        var product = Path.GetFileName(productFolder);

        if (_variableFolders.Any(named => LongPath.Contains(productFolder, named)))
        {
            return TopazInstallStanding.Installed;
        }

        var unknown = _registryIncomplete;

        foreach (var programs in VendorFolders)
        {
            switch (LongPath.ProbeDirectory(Path.Combine(programs, product)))
            {
                case PathPresence.Present:
                    return TopazInstallStanding.Installed;

                case PathPresence.Refused:
                    unknown = true;
                    break;
            }
        }

        if (_entries.Any(entry => Names(entry, product)))
        {
            return TopazInstallStanding.Installed;
        }

        return unknown ? TopazInstallStanding.Unknown : TopazInstallStanding.NotInstalled;
    }

    /// <summary>
    /// Whether an entry is this product's: its install folder has the product's name, or its display
    /// name is the product's name with at most a version after it. The version has to start with a
    /// digit, so <c>Topaz Photo AI 3.1.2</c> is not taken for <c>Topaz Photo</c>.
    /// </summary>
    private static bool Names(UninstallValues entry, string product)
    {
        if (InstallLocation.Of(entry) is { Kind: InstallLocationKind.Path, Text: var location }
            && Path.GetFileName(location).Equals(product, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return entry.Text("DisplayName") is { } name
            && name.StartsWith(product, StringComparison.OrdinalIgnoreCase)
            && VersionSuffix().IsMatch(name.AsSpan(product.Length));
    }

    [GeneratedRegex(@"^(\s+v?\d.*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex VersionSuffix();
}
