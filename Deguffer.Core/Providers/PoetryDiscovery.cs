using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where Poetry says its two directories are on this machine.
/// </summary>
/// <param name="CacheRoot">
/// The cache directory, which is the folder Deguffer reaches into. Never null: where Poetry cannot
/// answer, this is the documented default, and the provider still checks it exists before acting.
/// </param>
/// <param name="Environments">
/// Where Poetry keeps its virtual environments. Never null for the same reason, and the reason this
/// record has two fields rather than one: <c>virtualenvs.path</c> defaults to a child of
/// <paramref name="CacheRoot"/> and can be pointed anywhere, so the two are separate answers that
/// happen to overlap by default. A provider that derived this from the cache root would be asserting
/// the default rather than describing the machine.
/// </param>
public sealed record PoetryLocations(string CacheRoot, string Environments);

/// <summary>
/// Asks Poetry about itself. Separate from the provider for the reason <see cref="VcpkgDiscovery"/>
/// is: one type answers "where is this tool?" and the other answers "what inside it may go".
///
/// <para>Everything here is Poetry's own answer to a question, arriving through a subprocess and
/// through nothing else. That is what makes it one responsibility rather than a bag of helpers: the
/// three lookups share a failure mode — Poetry may be an older version, may colourise, may print
/// something that is not a path at all — and the handling of that belongs in one place.</para>
///
/// <para>It takes no <see cref="IUserEnvironment"/>, and the fallback location is an argument
/// instead. Where Poetry keeps its folders in a profile is what the provider's §5.2 declarations are
/// written against, so that knowledge stays in one file rather than being stated in two.</para>
/// </summary>
public sealed partial class PoetryDiscovery(IProcessRunner runner)
{
    /// <summary>
    /// What a cache name has to look like before it is pasted into a command line. A repository name
    /// comes from a <c>pyproject.toml</c> source and reaches Deguffer as a directory name, so it is
    /// somebody else's string arriving in the arguments of a process this tool starts. A name
    /// carrying a space or a quote would change which command runs, and the honest answer to one is
    /// to leave that cache to Poetry rather than to guess at quoting it.
    /// </summary>
    [GeneratedRegex(@"\A[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainCacheName();

    private PoetryLocations? _located;

    /// <summary>
    /// Both settings are configuration, and <c>poetry config</c> is how either is changed, so a
    /// remembered pair would describe directories Poetry has stopped using.
    /// </summary>
    public void Invalidate() => _located = null;

    /// <summary>
    /// Poetry's two directories, asked once and remembered for the life of a planning pass.
    ///
    /// <para>Two lookups rather than one because Poetry offers no combined form: <c>config --list</c>
    /// prints the raw setting with the resolved value in a trailing comment, which is a parse of a
    /// display format, while asking for one key prints the resolved value on its own.</para>
    /// </summary>
    /// <param name="poetry">The Poetry executable, already resolved on <c>PATH</c>.</param>
    /// <param name="defaultCacheRoot">
    /// Where Poetry keeps its cache when it has not been asked, used when it cannot answer.
    /// <c>cache-dir</c> in <c>config.toml</c> and <c>POETRY_CACHE_DIR</c> both move it, so this is a
    /// last resort rather than an assumption.
    /// </param>
    /// <param name="ct">Cancellation for the two subprocess calls.</param>
    public async Task<PoetryLocations> DiscoverAsync(
        string poetry,
        string defaultCacheRoot,
        CancellationToken ct)
    {
        if (_located is not null)
        {
            return _located;
        }

        var cacheRoot = await AskAsync(poetry, "cache-dir", ct).ConfigureAwait(false) ?? defaultCacheRoot;
        var environments = await AskAsync(poetry, "virtualenvs.path", ct).ConfigureAwait(false)
            ?? Path.Combine(cacheRoot, "virtualenvs");

        return _located = new PoetryLocations(cacheRoot, environments);
    }

    /// <summary>
    /// The caches Poetry's own <c>clear</c> will accept, asked of Poetry rather than read off the
    /// disk. The names are what <c>cache clear</c> validates against its repository cache directory,
    /// so taking them from the tool that is about to be handed them back is what keeps the two in
    /// step if Poetry ever changes how a repository maps to a directory.
    ///
    /// <para>Empty is an ordinary answer: Poetry prints "No caches found" and still exits zero on a
    /// machine that has resolved nothing. It is deliberately not remembered — one planning pass asks
    /// once, and a second cache to invalidate would be a second thing to get wrong.</para>
    /// </summary>
    public async Task<IReadOnlyList<string>> ListCachesAsync(string poetry, CancellationToken ct)
    {
        var outcome = await runner.RunAsync(poetry, "cache list --no-ansi", ct).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return [];
        }

        return
        [
            .. outcome.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => PlainCacheName().IsMatch(line))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// One <c>poetry config</c> lookup, normalised, or null where Poetry reported something that is
    /// not a path.
    ///
    /// <para><c>--no-ansi</c> because Poetry colourises its output when it believes it is writing to
    /// a terminal, and the escape sequences would land inside the parsed path.</para>
    ///
    /// <para>Through <see cref="LongPath.Configured"/> rather than used as it arrived: a trailing
    /// separator would make the leaf name empty, and a value carrying <c>..</c> would compare equal
    /// to nothing and walk straight past the containment checks the provider makes on it. A setting
    /// Poetry has no value for prints as <c>null</c>, which is not a rooted path and so falls back
    /// correctly.</para>
    /// </summary>
    private async Task<string?> AskAsync(string poetry, string key, CancellationToken ct)
    {
        var outcome = await runner.RunAsync(poetry, $"config {key} --no-ansi", ct).ConfigureAwait(false);

        return outcome.Succeeded
            ? outcome.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(LongPath.Configured)
                .LastOrDefault(path => path is not null)
            : null;
    }
}
