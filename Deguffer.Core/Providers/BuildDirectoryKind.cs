namespace Deguffer.Core.Providers;

/// <summary>
/// What makes a directory of a given name the build output of a given toolchain.
///
/// <para>§5.2's rule is that an unrecognised thing is Tier 4, and outside a tool's own root the only
/// thing a directory carries is its name — which is worth nothing. <c>build</c>, <c>target</c> and
/// <c>Library</c> are ordinary English words, and a directory called one of them is as likely to
/// hold a photographer's exports as a compiler's output. So a name is never the evidence here. The
/// evidence is the <em>project around it</em>: a manifest a build reads, and a marker the tool
/// itself writes.</para>
///
/// <para>This is <see cref="Safety.ContentSignature"/>'s idea moved up one level — that one asks
/// whether every file inside a directory matches a pattern, and this asks what stands beside the
/// directory. Unity is why: a <c>Library</c> may hold anything at all, and what proves it is Unity's
/// is that <c>Assets</c>, <c>Packages</c> and <c>ProjectSettings</c> sit next to it.</para>
///
/// <para>Every condition is a conjunction, and a missing one means unrecognised and untouched. The
/// cost of being wrong is asymmetric in the same way <see cref="Safety.DotNetIntermediateSignature"/>
/// says it is: a missed directory costs disk space, a wrong one costs work.</para>
/// </summary>
public sealed record BuildDirectoryKind
{
    /// <summary>
    /// The directory names discovery searches for — no evidence at all on their own.
    ///
    /// A list because one toolchain's output can go by more than one name and the choice is the
    /// developer's: a Python virtual environment is <c>.venv</c> or <c>venv</c> depending on who
    /// made it, and both are the same subject with the same evidence and the same cost.
    /// </summary>
    public required IReadOnlyList<string> DirectoryNames { get; init; }

    /// <summary>The names as the user would read them in a sentence.</summary>
    public string DisplayNames => DirectoryNames.Count == 1
        ? $"'{DirectoryNames[0]}'"
        : string.Join(" or ", DirectoryNames.Select(n => $"'{n}'"));

    /// <summary>Entries that must all sit beside the directory, in its project folder.</summary>
    public IReadOnlyList<string> RequiredSiblings { get; init; } = [];

    /// <summary>
    /// Entries of which at least one must sit beside the directory, where a toolchain has several
    /// interchangeable manifests. Empty means no such requirement.
    ///
    /// Python is the case: a virtual environment is only regenerable if something records what was
    /// installed into it, and that record is <c>requirements.txt</c> or <c>pyproject.toml</c> or
    /// <c>Pipfile</c> depending on the tooling. Without one, the environment is the only copy of
    /// its own contents, and it is not build output at all.
    /// </summary>
    public IReadOnlyList<string> AnyOfSiblings { get; init; } = [];

    /// <summary>
    /// Entries that must all be inside the directory — the marker the tool writes into its own
    /// output, which is the strongest evidence available because nothing else writes it.
    /// </summary>
    public IReadOnlyList<string> RequiredContents { get; init; } = [];

    /// <summary>
    /// Files the tool holds open while it is using this directory, relative to it. Handed to
    /// <see cref="Safety.ILiveTreeInspector"/>; see <see cref="Safety.LiveTreeQuery.LockFileNames"/>
    /// for why they are declared rather than discovered.
    /// </summary>
    public IReadOnlyList<string> LockFiles { get; init; } = [];
}
