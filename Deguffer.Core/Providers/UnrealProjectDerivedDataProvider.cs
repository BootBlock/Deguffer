using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// An Unreal project's own <c>DerivedDataCache</c> folder: compiled shaders and prepared asset
/// data, including the <c>Compressed.ddp</c> pack some projects carry.
///
/// <para><b>Tier 2.</b> Everything in a derived-data cache is by definition derived from the
/// project's content, and Epic's own advice for clearing one by hand is to delete the folder and let
/// it fill again. What makes it Tier 2 rather than Tier 1 is the price: the next time the project
/// opens, the editor compiles its shaders again, which on a large project is a pass measured in tens
/// of minutes.</para>
///
/// <para>The machine-wide cache every project shares is a different location with a different
/// owner, and <see cref="UnrealDerivedDataCacheProvider"/> reaches it. This one is found only in the
/// source folders the user approved.</para>
///
/// <para><see cref="UnrealProjectLayout"/> holds what makes a folder an Unreal project, and why
/// <c>Saved</c> and <c>Binaries</c> are never targets.</para>
/// </summary>
public sealed class UnrealProjectDerivedDataProvider : BuildDirectoryProvider
{
    private static readonly BuildDirectoryKind UnrealProjectDerivedData = UnrealProjectLayout.Kind("DerivedDataCache");

    public UnrealProjectDerivedDataProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(UnrealProjectDerivedData, roots, discovery, liveTrees, environment, runner, inspector, scanner)
    {
    }

    public override string Id => "unreal-project-ddc";

    public override string Name => "Unreal project derived data cache";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next time you open the project, Unreal compiles its shaders and prepares its assets "
        + "again. On a large project that can take tens of minutes. Nothing is lost: all of it is "
        + "derived from the project's content.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Unreal Editor",
        Publisher = "Epic Games",
        Purpose = "Unreal keeps the compiled shaders and prepared forms of a project's assets in a "
            + "cache, so the editor does not have to produce them again each time the project "
            + "opens. A project can keep its own copy of that cache in its DerivedDataCache folder.",
        Recommendation = "Epic's own advice is to delete the folder and let it fill again, but "
            + "the refill is a shader compile, and on a large project that takes a long time.",
    };

    protected override string Subject => "Unreal derived data";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for "
        + "Unreal projects inside them, and nowhere else.";

    protected override IReadOnlyList<string> ConflictingProcessNames => UnrealProjectLayout.ProcessNames;
}
