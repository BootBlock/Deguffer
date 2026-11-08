using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// How every git command Deguffer starts is run, so that how git's output is read is decided once.
///
/// <para><b>Git for Windows writes paths and configuration values in UTF-8, whatever the console's
/// code page.</b> Read in the console's code page, as a tool's output is by default, a non-ASCII path
/// <c>git ls-files</c> listed reached <see cref="TrackedFileCheck"/> in a code page such as 850 and
/// matched no candidate, so a directory holding tracked files under such a name read as holding none.
/// Only a process with a console reads that way: the app has none, while the benchmark and the test
/// host do.</para>
/// </summary>
internal static class GitRun
{
    public const ToolOutputEncoding Output = ToolOutputEncoding.Utf8;

    /// <summary>A read-only git query (<see cref="ToolRun.Query"/>) whose output is read as git writes it.</summary>
    public static readonly ToolRun Query = ToolRun.Query.WritingIn(Output);
}
