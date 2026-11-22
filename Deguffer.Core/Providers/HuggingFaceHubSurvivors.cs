using System.Collections.Frozen;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// §5.6 for the Hugging Face hub cache. Everything in the hub cache beside the repositories offered is
/// the point: the tool's own files, the shared store every repository may link into, each repository
/// held back, and anything the tool did not list, which has the same shape as the folders removed
/// beside it. Then the Hugging Face folder above, where the access tokens are.
///
/// <para>The shared store is asserted standing and nothing more. <c>hf cache rm</c> removes the
/// files in it that only the removed repository used, and keeps the store.</para>
/// </summary>
internal static class HuggingFaceHubSurvivors
{
    /// <summary>
    /// What sits at the top of the hub cache beside the repositories, with why each survives:
    /// <c>huggingface_hub</c>'s own tag, locks and shared store, and the <c>version.txt</c> Transformers
    /// writes there to record which layout it last moved the cache to.
    /// </summary>
    public static readonly FrozenDictionary<string, string> CacheFiles = new Dictionary<string, string>
    {
        ["CACHEDIR.TAG"] = "The tag that tells backup tools this folder is a cache.",
        ["version.txt"] = "The record Transformers keeps of which layout it last moved the cache to.",
        [".locks"] = "The locks huggingface_hub takes while it downloads.",
        ["blobs"] = "The shared store of files several repositories link into. Only 'hf cache rm' may remove a file from it.",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <param name="hub">The hub cache the tool reported and listed.</param>
    /// <param name="reported">What <c>hf env</c> said, which names the access tokens and the assets folder.</param>
    /// <param name="entries">Everything in the hub cache, as the plan listed it.</param>
    /// <param name="offered">The folder of each repository the plan offers, which alone may go.</param>
    /// <param name="held">Each repository held back because a program is using it, and why.</param>
    public static IReadOnlyList<(string Path, string Reason)> Of(
        string hub,
        HuggingFaceEnvironment reported,
        IReadOnlyList<FileSystemInfo> entries,
        IEnumerable<string> offered,
        IReadOnlyList<(string Path, string Reason)> held)
    {
        var excluded = new HashSet<string>(offered, StringComparer.OrdinalIgnoreCase);
        var heldReasons = held.ToDictionary(item => item.Path, item => item.Reason, StringComparer.OrdinalIgnoreCase);

        var inCache = entries
            .Select(entry => LongPath.Display(entry.FullName))
            .Where(path => !excluded.Contains(path))
            .Select(path => (path, CacheFiles.GetValueOrDefault(Path.GetFileName(path))
                ?? heldReasons.GetValueOrDefault(path)
                ?? "Not a repository Deguffer offers, so it must survive."));

        // The Hugging Face folder is the one the tool keeps its tokens in. Its other children are
        // asserted only where it holds the hub cache, because a hub cache moved on its own sits beside
        // folders that are nothing of the tool's.
        var home = reported.Credentials.Select(Path.GetDirectoryName).OfType<string>().FirstOrDefault();
        var parent = Path.GetDirectoryName(hub);
        var besideHub = home is not null && parent is not null && home.Equals(parent, StringComparison.OrdinalIgnoreCase)
            ? FolderEntries.Of(parent)?
                .Select(entry => LongPath.Display(entry.FullName))
                .Where(path => !path.Equals(hub, StringComparison.OrdinalIgnoreCase))
                .Select(path => (path, "Beside the hub cache in the Hugging Face folder, and nothing removes it."))
                ?? []
            : [];

        return
        [
            (hub, "The Hugging Face hub cache must survive: only the repositories chosen are removed from it."),
            .. reported.Credentials.Select(file => (file, "Your Hugging Face access tokens.")),
            .. reported.Assets is { } assets ? [(assets, "Files libraries downloaded for themselves beside the hub cache.")] : Array.Empty<(string, string)>(),
            .. inCache,
            .. besideHub,
        ];
    }
}
