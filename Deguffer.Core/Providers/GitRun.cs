using System.Collections.Frozen;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// How every git command Deguffer starts is run, so that how git's output is read, and which of the
/// variables Deguffer inherited git is given, are decided once.
///
/// <para><b>Git for Windows writes paths and configuration values in UTF-8, whatever the console's
/// code page.</b> Read in the console's code page, as a tool's output is by default, a non-ASCII path
/// <c>git ls-files</c> listed reached <see cref="TrackedFileCheck"/> in a code page such as 850 and
/// matched no candidate, so a directory holding tracked files under such a name read as holding none.
/// Only a process with a console reads that way: the app has none, while the benchmark and the test
/// host do.</para>
///
/// <para><b>git starts without the variables that tell it which repository, index and objects to
/// read.</b> git reads every one of these over <c>-C</c> and the repository it finds, and
/// <c>GIT_INDEX_FILE</c>, <c>GIT_OBJECT_DIRECTORY</c> and <c>GIT_COMMON_DIR</c> over
/// <c>--git-dir</c> and <c>--work-tree</c> as well, so a value Deguffer inherited from a script or a
/// tool that exported it for its own work sends the question to that work instead. Observed with git
/// 2.56 for Windows: an inherited <c>GIT_DIR</c> naming another repository, <c>GIT_WORK_TREE</c>
/// naming the folder above the repository, or <c>GIT_INDEX_FILE</c> naming a file that is not there,
/// made <c>ls-files</c> list nothing and exit 0, and a successful empty answer reads as "nothing tracked".
/// <c>GIT_COMMON_DIR</c> would hand Git LFS's prune another repository's refs to decide what to keep.</para>
///
/// <para>The list is the one git itself clears before it runs a command in another repository, as in
/// a submodule, and prints for <c>git rev-parse --local-env-vars</c>. It includes
/// <c>GIT_CONFIG_PARAMETERS</c> and <c>GIT_CONFIG_COUNT</c>, the settings a <c>git -c</c> further
/// up handed down, because those too were meant for that other work. The user's own configuration
/// files are read as ever.</para>
/// </summary>
internal static class GitRun
{
    public const ToolOutputEncoding Output = ToolOutputEncoding.Utf8;

    /// <summary>The variables git starts without, as git 2.56 lists them.</summary>
    public static readonly FrozenSet<string> Unset = FrozenSet.Create(
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

    /// <summary>
    /// A read-only git query (<see cref="ToolRun.Query"/>) whose output is read as git writes it, and
    /// which starts without <see cref="Unset"/>.
    /// </summary>
    public static readonly ToolRun Query = ToolRun.Query.WritingIn(Output).Without(Unset);
}
