using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where <c>huggingface_hub</c> keeps its folders as <c>constants.py</c> resolves them from the
/// variables, for what Explore and a duplicate search refuse without asking <c>hf</c>.
///
/// <para>The hub cache is <c>HF_HUB_CACHE</c>, then <c>HUGGINGFACE_HUB_CACHE</c>, then
/// <c>HF_HOME\hub</c>. <c>HF_HOME</c> is the variable, then <c>XDG_CACHE_HOME\huggingface</c>, then
/// <c>~\.cache\huggingface</c>. Python also expands <c>~</c> and variables inside each value, which
/// this does not: such a value names no full path here, so <see cref="ConfiguredFolder"/> declines it
/// and no folder is declared for it. The plan never reads these, because it asks <c>hf env</c>.</para>
/// </summary>
/// <param name="Home">The Hugging Face folder, or null where its setting is declined.</param>
/// <param name="Hub">The hub cache, or null where its setting is declined.</param>
internal readonly record struct HuggingFaceLocations(string? Home, string? Hub)
{
    /// <summary>Moves the hub cache on its own. Read first.</summary>
    public const string HubCacheVariable = "HF_HUB_CACHE";

    /// <summary>The name <see cref="HubCacheVariable"/> had before, read where it is not set.</summary>
    public const string LegacyHubCacheVariable = "HUGGINGFACE_HUB_CACHE";

    /// <summary>Moves everything <c>huggingface_hub</c> keeps, the hub cache in its <c>hub</c> folder.</summary>
    public const string HomeVariable = "HF_HOME";

    /// <summary>Read for <see cref="HomeVariable"/>'s default, which is its <c>huggingface</c> folder.</summary>
    public const string XdgCacheVariable = "XDG_CACHE_HOME";

    /// <summary>Where <c>huggingface_hub</c> keeps everything when no variable moves it.</summary>
    public static string DefaultHome(IUserEnvironment environment) =>
        Path.Combine(environment.UserProfile, ".cache", "huggingface");

    /// <summary>Where the hub cache is when no variable moves it.</summary>
    public static string DefaultHub(IUserEnvironment environment) => Path.Combine(DefaultHome(environment), "hub");

    /// <summary>Both folders as the variables read through <paramref name="reading"/> name them.</summary>
    public static HuggingFaceLocations Resolve(
        VariableReading reading,
        IUserEnvironment environment,
        ISystemDirectories machine,
        IVolumeInventory volumes)
    {
        var defaultHome = DefaultHome(environment);
        var defaultHub = DefaultHub(environment);

        var home = IsSet(HomeVariable)
            ? ConfiguredFolder.FromVariable(reading, HomeVariable, defaultHome, environment, machine, volumes).Folder
            : ConfiguredFolder.FromVariable(reading, XdgCacheVariable, defaultHome, environment, machine, volumes, within: "huggingface").Folder;

        var hub = IsSet(HubCacheVariable)
            ? ConfiguredFolder.FromVariable(reading, HubCacheVariable, defaultHub, environment, machine, volumes).Folder
            : IsSet(LegacyHubCacheVariable)
                ? ConfiguredFolder.FromVariable(reading, LegacyHubCacheVariable, defaultHub, environment, machine, volumes).Folder
                : home is null ? null : Path.Combine(home, "hub");

        return new HuggingFaceLocations(home, hub);

        bool IsSet(string variable) => !string.IsNullOrWhiteSpace(ConfiguredFolder.Read(reading, variable, environment));
    }
}
