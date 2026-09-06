using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A Python project's virtual environment — an interpreter and the packages installed into it.
///
/// <para><b>Two conditions, and each closes a different way of being wrong.</b> <c>pyvenv.cfg</c>
/// inside is what PEP 405 makes a virtual environment, so it proves the directory is one rather than
/// somebody's folder called <c>venv</c>. A dependency manifest beside it is what makes the
/// environment <em>regenerable</em>: with no record of what was installed, the environment is the
/// only copy of its own contents, and removing it destroys information rather than freeing space.
/// Neither condition alone is enough, and a directory failing either is left alone.</para>
///
/// <para>Tier 2, and the honest form of that claim has a caveat in it: recreating the environment
/// means running the install again, and a manifest can be incomplete — a hand-maintained
/// <c>requirements.txt</c> that never had <c>pip freeze</c> run over it, or a package installed
/// once and never written down. So the sentence the user reads says what has to be re-run rather
/// than promising the result is identical.</para>
///
/// <para><b>The environments worth the most are the ones nobody thinks to declare.</b> A
/// machine-learning or image-generation application installs into its own folder, not into a code
/// directory, and the environment it builds there holds a CUDA-enabled <c>torch</c> and its
/// neighbours — gigabytes, against the hundreds of megabytes a web project's environment costs. Both
/// of this provider's conditions hold for it, and its tier is unchanged. It is simply never searched
/// for, because a search runs only inside the folders the user approved, and nobody thinks of an
/// application's install folder as source. Widening the search is not the answer: the boundary is a
/// safety property, and §5.2's whole argument is that a cheap answer is not permission. Saying so is
/// the answer, which is what <see cref="AiEnvironmentsLiveElsewhere"/> is for.</para>
///
/// <para>An environment whose interpreter is running is the case the live-tree check catches without
/// any Python-specific knowledge: an activated environment runs
/// <c>&lt;env&gt;\Scripts\python.exe</c>, so the process's own executable is inside the directory
/// being considered.</para>
/// </summary>
public sealed class PythonVirtualEnvironmentProvider : BuildDirectoryProvider
{
    private static readonly BuildDirectoryKind VirtualEnvironment = new()
    {
        DirectoryNames = [".venv", "venv"],
        RequiredContents = ["pyvenv.cfg"],
        AnyOfSiblings = ["requirements.txt", "pyproject.toml", "Pipfile", "setup.py", "environment.yml"],
    };

    public PythonVirtualEnvironmentProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(VirtualEnvironment, roots, discovery, liveTrees, environment, runner, inspector, scanner)
    {
    }

    public override string Id => "python-venv";

    public override string Name => "Python virtual environments";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The project will not run until the environment is created again and its dependencies " +
        "installed from the manifest beside it. That needs the network unless pip's cache still " +
        "holds the wheels. A manifest only lists what somebody wrote down, so check it covers what " +
        "you had installed before removing an environment you still use.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Python's venv, and the tools built on top of it",
        Publisher = "the Python Software Foundation",
        Purpose = "A virtual environment is a private copy of an interpreter and the packages one "
            + "project installs, kept in a .venv or venv folder beside that project so its "
            + "dependencies collide with nothing else.",
        Recommendation = "One caveat comes with it: recreating the environment means running the "
            + "install again, and a hand-maintained requirements file may not list everything that "
            + "was in it. Deguffer offers one only where a dependency manifest sits beside the "
            + "project, and only inside the folders listed under Settings, Source folders. "
            + AiEnvironmentsLiveElsewhere,
    };

    protected override string Subject => "a Python virtual environment";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for " +
        "Python virtual environments inside them, and nowhere else. " + AiEnvironmentsLiveElsewhere;

    /// <summary>
    /// The sentence that closes the gap described in the class remarks. Written once because it is
    /// said in two places — the explanation behind the row, and the guidance shown when nothing has
    /// been approved — and a fact stated in one wording here and another there reads as two claims.
    /// </summary>
    private const string AiEnvironmentsLiveElsewhere =
        "The largest environment on a machine usually belongs to a machine-learning or "
        + "image-generation application, which holds gigabytes of packages such as torch and "
        + "installs itself outside any code folder, so add that application's own folder if you "
        + "want its environment considered.";
}
