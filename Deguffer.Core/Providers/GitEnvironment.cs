using System.Collections.Frozen;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// How every git Deguffer starts is run: without the variables that tell git which repository, index
/// and objects to read.
///
/// <para>git reads these over <c>-C</c>, <c>--git-dir</c> and the repository it finds, so a value
/// Deguffer inherited from a script or a tool that exported it for its own work sends every question
/// to that work instead. Observed with git 2.56 for Windows: an inherited <c>GIT_DIR</c> naming
/// another repository, or <c>GIT_INDEX_FILE</c> naming a file that is not there, made
/// <c>ls-files</c> list nothing and exit 0, and a successful empty answer reads as "nothing tracked".
/// <c>GIT_COMMON_DIR</c> would hand Git LFS's prune another repository's refs to decide what to keep.</para>
///
/// <para>The list is the one git itself clears before it runs a command in another repository, as in
/// a submodule, and prints for <c>git rev-parse --local-env-vars</c>. It includes
/// <c>GIT_CONFIG_PARAMETERS</c> and <c>GIT_CONFIG_COUNT</c>, the settings a <c>git -c</c> further
/// up handed down, because those too were meant for that other work. The user's own configuration
/// files are read as ever.</para>
/// </summary>
internal static class GitEnvironment
{
    /// <summary>The variables, as git 2.56 lists them.</summary>
    public static readonly FrozenSet<string> RepositoryVariables = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_CONFIG",
        "GIT_CONFIG_PARAMETERS",
        "GIT_CONFIG_COUNT",
        "GIT_OBJECT_DIRECTORY",
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_IMPLICIT_WORK_TREE",
        "GIT_GRAFT_FILE",
        "GIT_INDEX_FILE",
        "GIT_NO_REPLACE_OBJECTS",
        "GIT_REPLACE_REF_BASE",
        "GIT_PREFIX",
        "GIT_SHALLOW_FILE",
        "GIT_COMMON_DIR");

    /// <summary>A read-only git query, run as <see cref="ToolRun.Query"/> is.</summary>
    public static ToolRun Query { get; } = ToolRun.Query.Without(RepositoryVariables);
}
