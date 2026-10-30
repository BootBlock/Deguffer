using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>Where Conan says it keeps things, or why it could not be told, or why it is not believed.</summary>
internal abstract record ConanLocation
{
    /// <summary>Conan's home and its package store, as Conan reported them, in full form.</summary>
    public sealed record Found(string Home, string Store) : ConanLocation;

    /// <summary>Conan's answer could not be used, for the reason given.</summary>
    public sealed record Unknown(string Why) : ConanLocation;

    /// <summary>
    /// Conan is set to keep its home in a folder that is not examined as Conan's, for the reason
    /// given as the end of a sentence. The store was never asked about.
    /// </summary>
    /// <param name="Source">What named the home, for the user.</param>
    public sealed record HomeDeclined(string Home, string Source, string Why) : ConanLocation;

    /// <summary>
    /// Conan is set to keep its package store in a folder that is not examined as Conan's, for the
    /// reason given as the end of a sentence. The home is Conan's.
    /// </summary>
    public sealed record StoreDeclined(string Home, string Store, string Why) : ConanLocation;

    /// <summary>
    /// Conan was not asked, because the home it would work in was not there when this pass looked,
    /// and asking would have made one.
    /// </summary>
    public sealed record NotAsked : ConanLocation;

    /// <summary>
    /// Ask Conan for its home, then for the store in it. Only for a home known to be there: each
    /// question builds a <c>ConanAPI</c>, which creates a missing home.
    /// </summary>
    /// <param name="whyNotOwned">
    /// Why a folder Conan names must not be examined as Conan's, or null where it may. Asked of both
    /// answers, because a home is somewhere <c>CONAN_HOME</c> or a <c>.conanrc</c> put it, and a store
    /// is somewhere <c>core.cache:storage_path</c> did, and anything on the machine may have written
    /// either.
    /// </param>
    public static async Task<ConanLocation> AskAsync(
        IProcessRunner runner, string conan, Func<string, string?> whyNotOwned, CancellationToken ct)
    {
        var homeRun = await runner.RunAsync(conan, "config home", ToolRun.Query, ct).ConfigureAwait(false);

        if (!homeRun.Succeeded)
        {
            return new Unknown($"Conan did not say where its home is ({homeRun.Message}), so nothing is offered.");
        }

        if (ConanConfigOutput.ReadHome(homeRun.StandardOutput) is not { } home)
        {
            return new Unknown(
                $"Conan reported its home as '{homeRun.StandardOutput.Trim()}', which is not one full path, "
                + "so nothing is offered.");
        }

        if (whyNotOwned(home) is { } homeDeclined)
        {
            return new HomeDeclined(home, "Conan's own answer", homeDeclined);
        }

        var storeRun = await runner
            .RunAsync(conan, $"config show {ConanConfigOutput.StoragePathSetting}", ToolRun.Query, ct)
            .ConfigureAwait(false);

        if (!storeRun.Succeeded || ConanConfigOutput.ReadStore(storeRun.StandardOutput, home) is not { } store)
        {
            return new Unknown(
                $"Conan reports its home as {home}, but did not say where its package store is in a form "
                + $"Deguffer can use ({storeRun.Message}), so nothing is offered.");
        }

        return whyNotOwned(store) is { } storeDeclined
            ? new StoreDeclined(home, store, storeDeclined)
            : new Found(home, store);
    }
}
