using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// An Unreal project's <c>Intermediate</c> folder: what Unreal Build Tool and the editor produce on
/// the way to a build, and the Visual Studio project files the solution points at.
///
/// <para><b>Tier 2.</b> All of it is rebuilt from the project, and nothing in it is anybody's only
/// copy. The usual Unreal <c>.gitignore</c> is no evidence of that, because it excludes
/// <c>Saved</c> too, and <c>Saved</c> holds the autosaves. The price is the rebuild:
/// a C++ project's next build compiles its code from scratch, and the solution beside the project
/// shows its projects as unavailable until the project files are generated again from the
/// <c>.uproject</c>.</para>
///
/// <para>A separate row from <see cref="UnrealProjectDerivedDataProvider"/> because the two cost
/// different things to get back: this one a compile, that one a shader and asset pass. A row that
/// took both would state one price for two.</para>
///
/// <para><see cref="UnrealProjectLayout"/> holds what makes a folder an Unreal project, and why
/// <c>Saved</c> and <c>Binaries</c> are never targets.</para>
/// </summary>
public sealed class UnrealIntermediateProvider : BuildDirectoryProvider
{
    private static readonly BuildDirectoryKind UnrealIntermediate = UnrealProjectLayout.Kind("Intermediate");

    public UnrealIntermediateProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(UnrealIntermediate, roots, discovery, liveTrees, environment, runner, inspector, scanner)
    {
    }

    public override string Id => "unreal-intermediate";

    public override string Name => "Unreal project intermediate files";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next build of a C++ project compiles its code again from the start, and its Visual "
        + "Studio solution shows the projects as unavailable until you choose Generate Visual Studio "
        + "project files on the .uproject. A Blueprint-only project opens as before. Your content, "
        + "code, settings and autosaves are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Unreal Editor and Unreal Build Tool",
        Publisher = "Epic Games",
        Purpose = "Unreal keeps what it produces on the way to a build in a project's Intermediate "
            + "folder: compiled object files, generated code, and the Visual Studio project files "
            + "the solution beside the project points at.",
        Recommendation = "All of it is rebuilt from the project. A C++ project then builds from "
            + "scratch, and its project files have to be generated again. Deguffer never touches "
            + "the Saved folder beside it, which holds the editor's autosaves.",
    };

    protected override string Subject => "Unreal intermediate build files";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for "
        + "Unreal projects inside them, and nowhere else.";

    protected override IReadOnlyList<string> ConflictingProcessNames => UnrealProjectLayout.ProcessNames;
}
