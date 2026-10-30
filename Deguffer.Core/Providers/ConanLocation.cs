using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>Where Conan says it keeps things, or why it could not be told, or why it is not believed.</summary>
internal abstract record ConanLocation
{
    /// <summary>
    /// Conan's home and its package store, as Conan reported them and as Deguffer worked them out, in
    /// full form.
    /// </summary>
    /// <param name="Configuration">
    /// The home's configuration files as they were once Conan had answered, which is what Conan reads
    /// at the clean.
    /// </param>
    public sealed record Found(string Home, string Store, ConanConfFiles Configuration) : ConanLocation;

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
    /// Ask Conan for its home, then for the store in it, and hold each answer to what Deguffer worked
    /// out without Conan. Only for a home known to be there and a store already judged: each question
    /// builds a <c>ConanAPI</c>, which creates a missing home and makes the store and its database.
    ///
    /// <para><b>Why the answers must agree.</b> The home and the store were judged from Deguffer's own
    /// reading, and the question itself made the store. An answer that names another folder means
    /// that reading is not Conan's, so the folder Conan used was never judged.</para>
    ///
    /// <para><b>Why the configuration is read again afterwards.</b> Conan's first command writes a
    /// default <c>global.conf</c> where the home has none (<c>load_global_conf</c> in
    /// <c>conan/internal/model/conf.py</c>), so the files as they were before Conan started are not the
    /// files the clean will find. Held to that earlier reading, the clean would refuse every time. It
    /// is held to the files as they are once Conan has answered, provided they still name the store
    /// that was judged before Conan started.</para>
    /// </summary>
    /// <param name="own">The home as Deguffer worked it out, and what named it.</param>
    /// <param name="configured">The store the home's configuration names, and the files it was read from.</param>
    /// <param name="whyNotOwned">
    /// Why a folder Conan names must not be examined as Conan's, or null where it may. Asked of both
    /// answers, because a home is somewhere <c>CONAN_HOME</c> or a <c>.conanrc</c> put it, and a store
    /// is somewhere <c>core.cache:storage_path</c> did, and anything on the machine may have written
    /// either.
    /// </param>
    public static async Task<ConanLocation> AskAsync(
        IProcessRunner runner,
        string conan,
        ConanHomeAnswer.Found own,
        ConanConfReading.Plain configured,
        Func<string, string?> whyNotOwned,
        CancellationToken ct)
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

        if (!SameFolder(home, own.Home))
        {
            return new Unknown(
                $"Conan reports its home as {home}, but by {own.Source} it is {own.Home}, so the folder Conan "
                + "uses is not the one Deguffer checked, and nothing is offered.");
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

        if (whyNotOwned(store) is { } storeDeclined)
        {
            return new StoreDeclined(home, store, storeDeclined);
        }

        if (!SameFolder(store, configured.Store))
        {
            return new Unknown(
                $"Conan reports its package store as {store}, but its configuration in {home} names "
                + $"{configured.Store}, so the folder Conan uses is not the one Deguffer checked, and nothing is offered.");
        }

        return ConanGlobalConf.Read(own.Home) is ConanConfReading.Plain answered && SameFolder(answered.Store, configured.Store)
            ? new Found(home, store, answered.Files)
            : new Unknown(
                $"Conan's configuration in {home} no longer names {configured.Store}, the package store Deguffer "
                + "checked before Conan started, or could not be read again, so nothing is offered.");
    }

    /// <summary>Whether two full paths name one folder, as Windows compares names.</summary>
    public static bool SameFolder(string one, string two) => string.Equals(
        Path.TrimEndingDirectorySeparator(LongPath.Display(one)),
        Path.TrimEndingDirectorySeparator(LongPath.Display(two)),
        StringComparison.OrdinalIgnoreCase);
}
