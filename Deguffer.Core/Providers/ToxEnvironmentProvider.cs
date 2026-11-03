using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A project's <c>.tox</c>: the virtual environments tox builds to run a project's tests under each
/// interpreter and configuration it names.
///
/// <para><b>Recognised by the tag tox writes, read.</b> tox writes <c>CACHEDIR.TAG</c> into its work
/// directory where it is missing, on every environment it sets up, with the line "created by tox"
/// (<c>ensure_cachedir_tag</c> in
/// <see href="https://github.com/tox-dev/tox/blob/main/src/tox/util/path.py">path.py</see>, called
/// from <c>tox_env/api.py</c>). It writes through Python's text mode, so on Windows each line ends in
/// <c>\r\n</c>. tox tags a work directory that already exists, so the tag says tox has used the folder
/// rather than that tox made it; that is why the name, the tag's own line and a tox configuration
/// beside it are all required. The work directory defaults to <c>.tox</c> in the folder holding the
/// configuration, <c>tox.ini</c>, <c>tox.toml</c>, <c>pyproject.toml</c> or <c>setup.cfg</c>
/// (<c>SOURCE_TYPES</c> in
/// <see href="https://github.com/tox-dev/tox/blob/main/src/tox/config/source/discover.py">discover.py</see>).
/// A <c>.tox</c> without the tag is declined, which costs disk space rather than data.</para>
///
/// <para><b>Tier 2.</b> Every environment is rebuilt by the next run, which installs each one's
/// dependencies again, from the network unless pip's cache still holds them.</para>
/// </summary>
public sealed class ToxEnvironmentProvider : BuildDirectoryProvider
{
    private static readonly BuildDirectoryKind ToxWorkDirectory = new()
    {
        DirectoryNames = [".tox"],
        RequiredMarkers = [MarkerFile.CacheDirectoryTag("# This file is a cache directory tag created by tox.")],
        AnyOfSiblings = ["tox.ini", "tox.toml", "pyproject.toml", "setup.cfg"],
    };

    public ToxEnvironmentProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        IVolumeInventory? volumes = null)
        : base(ToxWorkDirectory, roots, discovery, liveTrees, environment, runner, inspector, scanner, volumes)
    {
    }

    public override string Id => "tox-environments";

    public override string Name => "tox environments";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next tox run builds every environment it uses again and installs each one's " +
        "dependencies, which needs the network unless pip's cache still holds them. Logs of earlier " +
        "runs go with the folder.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.ProjectBuildOutput,
        Application = "tox, the Python test automation tool",
        Publisher = "the tox-dev project",
        Purpose = "tox keeps a .tox folder in each project it runs, holding a virtual environment "
            + "for every interpreter and configuration the project tests against.",
        Recommendation = "Getting one back means installing every environment again. Deguffer offers "
            + "one only where tox's own cache tag is inside it and a tox configuration sits beside "
            + "it, and only inside the folders listed under Settings, Source folders.",
    };

    protected override string Subject => "a tox work folder";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for " +
        "tox environments inside them, and nowhere else.";
}
