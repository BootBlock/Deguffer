using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>Where Conan says it keeps things, or why it could not be told.</summary>
internal abstract record ConanLocation
{
    /// <summary>Conan's home and its package store, as Conan reported them, in full form.</summary>
    public sealed record Found(string Home, string Store) : ConanLocation;

    /// <summary>Conan's answer could not be used, for the reason given.</summary>
    public sealed record Unknown(string Why) : ConanLocation;

    /// <summary>
    /// Ask Conan for its home, then for the store in it. Only for a home known to be there: each
    /// question builds a <c>ConanAPI</c>, which creates a missing home.
    /// </summary>
    public static async Task<ConanLocation?> AskAsync(IProcessRunner runner, string conan, CancellationToken ct)
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

        var storeRun = await runner
            .RunAsync(conan, $"config show {ConanConfigOutput.StoragePathSetting}", ToolRun.Query, ct)
            .ConfigureAwait(false);

        return storeRun.Succeeded && ConanConfigOutput.ReadStore(storeRun.StandardOutput, home) is { } store
            ? new Found(home, store)
            : new Unknown(
                $"Conan reports its home as {home}, but did not say where its package store is in a form "
                + $"Deguffer can use ({storeRun.Message}), so nothing is offered.");
    }
}
