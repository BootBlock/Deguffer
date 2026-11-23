using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What Poetry's cache folder holds, as the Poetry row plans it and as §7.1 reads it from outside.
/// </summary>
internal static class PoetryCacheLayout
{
    /// <summary>
    /// The only children of Poetry's cache directory the Poetry row recognises. Anything else is
    /// Tier 4 by construction — see <see cref="DisposableChildSet"/>.
    ///
    /// <para><c>virtualenvs</c> is declared rather than omitted. An unrecognised child is already
    /// left alone, but it is left alone under a sentence saying Deguffer did not know what it was,
    /// and this is the one child whose contents the user most needs named.</para>
    ///
    /// <para><c>cache</c> is Tier 4 for a different reason, and it is not a claim that its contents
    /// are precious. §5.1 leaves Poetry's own command in charge of what is inside it, and that
    /// command empties the caches rather than removing the folder — so the folder is one this
    /// provider's §5.6 list asserts survived every run. §7.1 reads this same table, so classifying
    /// it as disposable here would let Explore remove a path the Storage page had just certified
    /// must survive, and the two routes would disagree about one directory.</para>
    /// </summary>
    public static readonly DisposableChildSet Children = new(
    [
        new ChildClassification(
            "artifacts",
            SafetyTier.RegenerableCache,
            "Package archives Poetry downloaded, and the wheels it built from source distributions. "
            + "The next install fetches or rebuilds them."),
        new ChildClassification(
            "cache",
            SafetyTier.DoNotTouch,
            "The folder holding the package metadata Poetry caches per repository. The caches inside "
            + "it are emptied by Poetry's own command rather than by removing the folder."),
        new ChildClassification(
            "virtualenvs",
            SafetyTier.DoNotTouch,
            "Every virtual environment Poetry has created, for every project on this machine. Each "
            + "one is a full dependency install rather than a cache, so none of them is ever removed."),
    ]);

    /// <summary>The one level the plan walks: the cache folder itself.</summary>
    public static readonly IReadOnlyList<CacheLevel> Levels = [new CacheLevel(string.Empty, Children)];

    /// <summary>
    /// A cache folder as §5.2 declares it, whether the default one or the one Poetry reports, which
    /// the two declarations must word and classify alike.
    /// </summary>
    public static ToolRoot CacheRoot(string path) => ToolRoot.Of(
        path,
        "This is Poetry's cache folder. Deguffer clears the caches inside it and nothing else, "
        + "because every virtual environment Poetry has created sits in here beside them.",
        Children);
}
