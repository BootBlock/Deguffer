using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A project's <c>.nox</c>: the virtual environments nox builds for each session in a project's
/// <c>noxfile.py</c>.
///
/// <para><b>Recognised by both files nox writes, read.</b> Each time it creates an environment, nox
/// writes into the folder holding them a <c>.gitignore</c> of <c>*</c> and a <c>CACHEDIR.TAG</c> of the
/// bare header, each only where it is missing (<c>_ensure_gitignore</c> and
/// <c>_ensure_cachedir_tag</c> in
/// <see href="https://github.com/wntrblm/nox/blob/main/nox/virtualenv.py">virtualenv.py</see>). Neither
/// names nox, so both are required whole, as nox writes them, with <c>noxfile.py</c>, the only file nox
/// reads its sessions from, beside the folder. nox runs from the folder holding <c>noxfile.py</c>, so
/// that is where the default <c>.nox</c> goes. One moved by <c>--envdir</c> is not found.</para>
///
/// <para><b>Tier 2.</b> Every session's environment is rebuilt by the next run, which installs its
/// dependencies again, from the network unless pip's cache still holds them.</para>
/// </summary>
public sealed class NoxEnvironmentProvider : BuildDirectoryProvider
{
    private static readonly BuildDirectoryKind NoxEnvironments = new()
    {
        DirectoryNames = [".nox"],
        RequiredMarkers =
        [
            MarkerFile.BareCacheDirectoryTag,
            new MarkerFile(".gitignore", ["*"], IsWholeFile: true),
        ],
        RequiredSiblings = ["noxfile.py"],
    };

    public NoxEnvironmentProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        IVolumeInventory? volumes = null)
        : base(NoxEnvironments, roots, discovery, liveTrees, environment, runner, inspector, scanner, volumes)
    {
    }

    public override string Id => "nox-environments";

    public override string Name => "nox environments";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next nox run builds every session's environment again and installs its dependencies, " +
        "which needs the network unless pip's cache still holds them.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.ProjectBuildOutput,
        Application = "nox, the Python test automation tool",
        Publisher = "the nox project",
        Purpose = "nox keeps a .nox folder beside each project's noxfile.py, holding a virtual "
            + "environment for every session it runs.",
        Recommendation = "Getting one back means installing every environment again. Deguffer offers "
            + "one only where the cache tag and .gitignore nox writes are inside it and a noxfile.py "
            + "sits beside it, and only inside the folders listed under Settings, Source folders.",
    };

    protected override string Subject => "a nox environment folder";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for " +
        "nox environments inside them, and nowhere else.";
}
