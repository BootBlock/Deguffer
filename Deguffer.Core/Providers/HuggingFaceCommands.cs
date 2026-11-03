using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The arguments every <c>hf</c> command here is given, in one place so the listing the plan reads
/// and the listing the run measures with cannot drift apart.
/// </summary>
internal static class HuggingFaceCommands
{
    /// <summary>Asks <c>huggingface_hub</c> for its version and the folders it resolved.</summary>
    public const string Environment = "env";

    /// <summary>
    /// Lists every repository in <paramref name="hubCache"/>. Warnings about a repository the tool
    /// cannot read go to standard error, so standard output is the JSON alone.
    /// </summary>
    public static string List(string hubCache) => $"cache ls --format json --cache-dir {Quoted(hubCache)}";

    /// <summary>
    /// Removes the repository <paramref name="id"/> names, every revision of it, from
    /// <paramref name="hubCache"/>. <c>--yes</c> because the JSON form raises rather than prompts
    /// without it, and the tool has nothing on its standard input to answer with.
    /// </summary>
    public static string Remove(string id, string hubCache) => $"cache rm {id} --cache-dir {Quoted(hubCache)} --format json --yes";

    /// <summary>
    /// A path as one argument. A folder cannot end in a backslash here, because
    /// <see cref="LongPath.Display"/> drops it, and a backslash before the closing quote would escape it.
    /// </summary>
    private static string Quoted(string path) => $"\"{LongPath.Display(path)}\"";
}
