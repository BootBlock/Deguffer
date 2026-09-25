using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What Autodesk's installers unpack and download into <c>C:\Autodesk</c> and leave behind: each
/// product release and each update extracts its own copy there, and nothing removes it afterwards.
/// Autodesk's own guidance puts the folder at 5 to 30 GB.
///
/// <para><b>Not measured.</b> No machine with <c>C:\Autodesk</c> was available when this was
/// written, so every rule here comes from Autodesk's knowledge base and from what administrators
/// report. <c>docs/cache-locations.md</c> lists the sources. Where they do not settle a name, the name
/// is not recognised, which costs an incomplete reclaim rather than a guess.</para>
///
/// <para><b>Tier 2, where the survey that proposed this said Tier 1.</b> Nothing re-creates an
/// installer payload, and Autodesk says a repair or an update asks for these files and must download
/// and extract the product again once they have gone. That is Tier 2's consequence, and it keeps a
/// location nobody has measured out of the default selection.</para>
///
/// <para><b>"Delete the entire folder" is Autodesk's advice in one article and wrong in another.</b>
/// The same knowledge base names <c>Network License Manager</c> as a licensing service running from
/// this folder, and gives <c>Deployments</c> as where an administrator keeps the deployment images
/// they built. So §5.2 holds here as it does for a tool's cache: the folder is never a target, only
/// the children named below are, and each of those two is named on the root and asserted to have
/// survived.</para>
///
/// <para><b>No administrator rights, inferred rather than measured.</b> A folder created at the top
/// of the system drive inherits modify rights for Authenticated Users, which is what
/// <see cref="GraphicsDriverInstallerProvider"/> measured for its neighbours. Nothing establishes
/// that Autodesk's installers set rights of their own. If one does, the removal is refused and
/// reported, which is the direction <see cref="EpicLauncherContentCacheProvider"/> explains is the
/// right one to be wrong in.</para>
/// </summary>
public sealed class AutodeskInstallerProvider : InstallerPayloadProvider
{
    private const string DownloadManagerSuffix = "_dlm";

    public AutodeskInstallerProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default,
            Candidates(system ?? SystemDirectories.Current))
    {
    }

    public override string Id => "autodesk-installers";

    public override string Name => "Autodesk installer files";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "Your installed Autodesk products keep working. Repairing one, or installing some updates, "
        + "asks for these files, so download and extract that product again from your Autodesk "
        + "Account first.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Autodesk product and update installers",
        Publisher = "Autodesk",
        Purpose = "An Autodesk installer extracts the whole product into C:\\Autodesk before it "
            + "installs anything, and leaves that copy behind. Every release and every update adds "
            + "its own, so the folder commonly reaches several gigabytes.",
        Recommendation = "The installed products do not run from these. Autodesk's guidance is that "
            + "the folder holds only installation files, and the cost of removing them is "
            + "downloading a product again before you repair or update it.",
    };

    protected override string Payloads => "installer files";

    protected override string NothingFound =>
        "No Autodesk installer has left its files in the places Deguffer knows about.";

    /// <summary>
    /// §5.3: Autodesk's current installer, and the program that fetches and starts it. While either
    /// runs, the folder it extracts into is the installation in progress.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["Installer", "AdODIS-installer"];

    private static IEnumerable<InstallerPayloadRoot> Candidates(ISystemDirectories system) =>
    [
        new InstallerPayloadRoot(
            "Autodesk",
            "Autodesk",
            system.SystemDrive,
            "Autodesk",
            Classify,
            [.. KeptChildren.Select(c => (c.Key, c.Value))]),
    ];

    /// <summary>
    /// A browser download extracts into a folder named after the file it came from, which always
    /// ends in <c>_dlm</c>, such as <c>AutoCAD_2024_English_Win_64bit_dlm</c>; updates are named the
    /// same way. <c>WI</c> and <c>IM</c> are where the installers keep what they download, and where
    /// a download from 2025 on extracts. What is kept for something else is refused first.
    /// </summary>
    private static ChildClassification Classify(string root, string name)
    {
        if (KeptChildren.TryGetValue(name, out var kept))
        {
            return new ChildClassification(name, SafetyTier.DoNotTouch, kept);
        }

        if (name.Equals("WI", StringComparison.OrdinalIgnoreCase))
        {
            return new ChildClassification(
                name,
                SafetyTier.RegenerableWithCost,
                "Where Autodesk's installers keep the packages they download, and where a product "
                + "downloaded from 2025 on is extracted.");
        }

        if (name.Equals("IM", StringComparison.OrdinalIgnoreCase))
        {
            return new ChildClassification(
                name,
                SafetyTier.RegenerableWithCost,
                "Where Autodesk's current installer keeps the packages it downloads.");
        }

        return name.Length > DownloadManagerSuffix.Length
            && name.EndsWith(DownloadManagerSuffix, StringComparison.OrdinalIgnoreCase)
            ? new ChildClassification(
                name,
                SafetyTier.RegenerableWithCost,
                "An Autodesk product or update, extracted by its installer and left behind after it "
                + "installed.")
            : new ChildClassification(
                name,
                SafetyTier.DoNotTouch,
                "It is not something Autodesk's installers are known to extract here.");
    }

    /// <summary>
    /// What Autodesk keeps in <c>C:\Autodesk</c> that is not a payload, and what each one is. Named
    /// on the root as well as refused, so §5.6 asserts each survived.
    /// </summary>
    private static readonly Dictionary<string, string> KeptChildren = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Network License Manager"] = "The network licence server installs here by default and "
            + "runs from it. Removing it stops every seat it licenses.",
        ["Deployments"] = "Where an administrator keeps the deployment images they built. Each "
            + "one is their own work, and Autodesk's installers do not re-create it.",
        ["Access"] = "Autodesk Access keeps the updates it downloads here and installs them in the "
            + "background, and nothing Deguffer can see says when it has finished with them.",
    };
}
