using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether a folder a setting names may be examined as one tool's own.
///
/// <para>The setting is something anything on the machine may have written. Examining a folder as a
/// tool's names everything else in it as a survivor (§5.6), and offers whatever in it carries one of the
/// tool's names. A drive root, one of the account's own folders, a folder holding a temporary folder,
/// or one holding a folder Windows is built out of is somewhere other rows legitimately remove things,
/// or somewhere the user keeps their files, and each of those removals would then read as a failure of
/// this one — and in Explore the whole of it would read as the tool's.</para>
///
/// <para>Every provider that is handed a folder by a setting asks this, rather than keeping a variant
/// of its own. It says only that the folder is not somewhere nothing may be. Whether what is in it is
/// the tool's is the provider's own evidence to establish.</para>
/// </summary>
internal static class ConfiguredFolder
{
    /// <summary>
    /// The folder a tool keeps where <paramref name="variable"/> moves it, <paramref name="defaultFolder"/>
    /// where it is not set, or the reason the value is declined.
    ///
    /// <para>A relative value is declined because the tool resolves it against the working directory of
    /// whichever process reads it, which Deguffer does not share, so there is no correct reading of it.
    /// A full path is declined where <see cref="WhyNotOwned"/> declines it.</para>
    /// </summary>
    public static Setting FromVariable(
        string variable,
        string defaultFolder,
        IUserEnvironment environment,
        ISystemDirectories machine)
    {
        var value = environment.GetEnvironmentVariable(variable)?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return new Setting(null, defaultFolder, null);
        }

        if (LongPath.Configured(value) is not { } configured)
        {
            return new Setting(value, null, "it is not a full path, so Deguffer cannot tell which folder it means.");
        }

        return WhyNotOwned(configured, environment, machine, TempRoots.Resolve(environment, machine).AccountFolders) is { } declined
            ? new Setting(value, null, declined)
            : new Setting(value, configured, null);
    }

    /// <summary>
    /// Why <paramref name="configured"/> must not be examined as a tool's own folder, as the end of a
    /// sentence, or null where it may be.
    /// </summary>
    /// <param name="configured">The folder, already through <see cref="LongPath.Configured"/>.</param>
    /// <param name="accountTempFolders">This account's own temporary folders.</param>
    public static string? WhyNotOwned(
        string configured,
        IUserEnvironment environment,
        ISystemDirectories machine,
        IReadOnlyList<string> accountTempFolders)
    {
        if (StandingFolders.WhyNotTaken(configured, environment, machine) is { } standing)
        {
            return standing;
        }

        var unaliased = LongPath.Unaliased(configured);

        return accountTempFolders
            .Append(Path.Combine(machine.WindowsDirectory, "Temp"))
            .Any(temp => temp.Length > 0 && LongPath.Contains(unaliased, LongPath.Unaliased(temp)))
            ? "it holds a temporary folder, where other rows remove things."
            : null;
    }

    /// <param name="Value">What the variable holds, trimmed, or null where it is not set.</param>
    /// <param name="Folder">The folder to examine as the tool's, or null where it is declined.</param>
    /// <param name="Declined">Why it is declined, as the end of a sentence, or null where it is not.</param>
    internal readonly record struct Setting(string? Value, string? Folder, string? Declined);
}
