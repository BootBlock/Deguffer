using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether a folder an environment variable names may be examined as one tool's own.
///
/// <para>The variable is something anything on the machine may have written. Examining a folder as a
/// tool's names everything else in it as a survivor (§5.6), and offers whatever in it carries one of the
/// tool's names. A drive root, a folder holding a temporary folder, or one holding a folder Windows is
/// built out of is somewhere other rows legitimately remove things, and each of those removals would
/// then read as a failure of this one — and in Explore the whole of it would read as the tool's.</para>
/// </summary>
internal static class ConfiguredFolder
{
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
        var unaliased = LongPath.Unaliased(configured);

        if (string.IsNullOrEmpty(Path.GetDirectoryName(unaliased)))
        {
            return "it is the root of a drive or a share.";
        }

        string[] mustNotHold =
        [
            .. accountTempFolders,
            Path.Combine(machine.WindowsDirectory, "Temp"),
            environment.UserProfile,
            environment.RoamingAppData,
            environment.LocalAppData,
            machine.WindowsDirectory,
            machine.ProgramData,
            machine.ProgramFiles,
            machine.ProgramFilesX86,
        ];

        return mustNotHold.Any(inside => inside.Length > 0 && LongPath.Contains(unaliased, LongPath.Unaliased(inside)))
            ? "it holds a temporary folder, or a folder Windows is built out of, where other rows remove things."
            : null;
    }
}
