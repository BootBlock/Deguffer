using System.Text;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where STM32CubeMX and STM32CubeIDE keep the firmware packages they download, on this account, and
/// which of those folders may be examined.
///
/// <para><b>Researched, not observed.</b> ST's tools default to <c>%USERPROFILE%\STM32Cube\Repository</c>.
/// STM32CubeMX moves it under <b>Help, Updater Settings</b>, and the folder it chose is the
/// <c>RepositoryPath</c> line of <c>.stm32cubemx\plugins\updater\updater.ini</c> in the profile, as an
/// open-source tool that reads the file parses it, removing double quotes around the value. No copy of
/// the file has been seen, so a value written in Java's properties form, <c>C\:\\Users\\...</c>, is read
/// as well as a plain path, and a quoted value as well as a bare one.</para>
///
/// <para><b>A folder STM32CubeIDE alone was told to use is not found.</b> The IDE keeps its own setting in
/// each workspace's preferences, and nothing outside a workspace lists the workspaces. Missing such a
/// repository costs an incomplete reclaim, never a wrong removal.</para>
///
/// <para><b>A repository not on a disk in this computer is withheld.</b> A team can share one repository
/// from a server, and another computer's STM32CubeMX may be installing a patch from a zip in it, which no
/// process check here can see.</para>
/// </summary>
/// <param name="Folders">The repositories to examine, each once however it was named.</param>
/// <param name="Declined">Links on the way to a repository, which are not followed, with why.</param>
/// <param name="Unreached">Folders on the way to a repository that Windows would not describe.</param>
/// <param name="Notes">What the user is told about a repository that was named and not examined.</param>
public sealed record Stm32CubeRepositories(
    IReadOnlyList<string> Folders,
    IReadOnlyList<(string Path, string Reason)> Declined,
    IReadOnlyList<string> Unreached,
    IReadOnlyList<PlanNote> Notes)
{
    /// <summary>The key STM32CubeMX's updater settings name the repository under.</summary>
    public const string SettingsKey = "RepositoryPath";

    /// <summary>
    /// §5.3. The programs that download, unpack and patch firmware packages. Both are published names,
    /// not checked against an installation. STM32CubeMX started as <c>java -jar</c> runs as Java and is
    /// not seen.
    /// </summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["STM32CubeMX", "stm32cubeide"];

    /// <summary>Far larger than an updater settings file, which holds a few lines.</summary>
    private const int MaximumSettingsBytes = 64 * 1024;

    private const string LinkReason = "A link rather than a folder, so what it points at was never classified.";

    /// <summary>The repository ST's tools use unless they are told otherwise.</summary>
    public static string DefaultFolder(IUserEnvironment environment) =>
        Path.Combine(environment.UserProfile, "STM32Cube", "Repository");

    /// <summary>The file STM32CubeMX's Updater Settings are saved in.</summary>
    public static string SettingsFile(IUserEnvironment environment) =>
        Path.Combine(environment.UserProfile, ".stm32cubemx", "plugins", "updater", "updater.ini");

    /// <summary>The default repository and the one STM32CubeMX's settings name, each vetted.</summary>
    public static Stm32CubeRepositories Find(IUserEnvironment environment, ISystemDirectories machine, IVolumeInventory volumes)
    {
        var folders = new List<(string Path, ReachedFolder Reached)>();
        var declined = new List<(string Path, string Reason)>();
        var unreached = new List<string>();
        var notes = new List<PlanNote>();

        var standard = DefaultFolder(environment);

        // Built from the profile, so a link at any folder on the way would put the removal somewhere
        // nothing established.
        if (DerivedPath.FirstObstacleBetween(environment.UserProfile, standard) is { } obstacle)
        {
            if (obstacle.IsLink)
            {
                declined.Add((obstacle.Path, LinkReason));
                notes.Add(CacheLevelWalk.Note(obstacle.Path));
            }
            else
            {
                unreached.Add(obstacle.Path);
                notes.Add(UnreadableRoot.UnreachedNote(obstacle.Path));
            }
        }
        else
        {
            Add(standard);
        }

        if (ReadSetting(SettingsFile(environment)) is { } value)
        {
            if (LongPath.Configured(value) is not { } named)
            {
                notes.Add(Declining(value, ConfiguredFolder.NotAFullPath));
            }
            else if (ConfiguredFolder.WhyNotOwned(
                    named, environment, machine, volumes, TempRoots.Resolve(environment, machine, volumes).AccountFolders) is { } why)
            {
                notes.Add(Declining(named, why));
            }
            else if (LongPath.IsReparsePoint(named) && LongPath.ProbeDirectory(named) is not PathPresence.Absent)
            {
                declined.Add((named, LinkReason));
                notes.Add(CacheLevelWalk.Note(named));
            }
            else
            {
                Add(named);
            }
        }

        return new Stm32CubeRepositories([.. folders.Select(folder => folder.Path)], declined, unreached, notes);

        // Said only where something is there, so a machine without ST's tools says nothing.
        void Add(string folder)
        {
            if (LongPath.ProbeDirectory(folder) is PathPresence.Absent)
            {
                return;
            }

            if (HostVolume.For(volumes, folder) is not { IsLocalDisk: true })
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Left the STM32Cube repository in '{LongPath.Display(folder)}' alone: it is not on a disk in "
                    + "this computer, so STM32CubeMX on another computer may be installing from it."));

                return;
            }

            var reached = ReachedFolder.At(folder, volumes);

            if (!folders.Exists(known => known.Reached.IsSameAs(reached)))
            {
                folders.Add((LongPath.Display(folder), reached));
            }
        }
    }

    private static PlanNote Declining(string value, string why) => new(
        PlanNoteSeverity.Information,
        $"STM32CubeMX's Updater Settings name '{value}' for its repository, and Deguffer leaves it alone: {why}");

    /// <summary>
    /// The repository <paramref name="file"/> names, or null where the file, the key or a value is
    /// missing, or the file is too large or not text.
    /// </summary>
    internal static string? ReadSetting(string file)
    {
        if (BoundedFile.Read(file, MaximumSettingsBytes) is not { } content)
        {
            return null;
        }

        string text;

        try
        {
            text = BoundedFile.StrictUtf8.GetString(content.Span);
        }
        catch (DecoderFallbackException)
        {
            // Not text, so not the settings file it is named for.
            return null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            if (line.IndexOf('=') is > 0 and var equals
                && line[..equals].Trim().Equals(SettingsKey, StringComparison.OrdinalIgnoreCase)
                && Unescaped(Unquoted(line[(equals + 1)..].Trim())) is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary><paramref name="value"/> without the double quotes a path holding a space may be written in.</summary>
    internal static string Unquoted(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Trim() : value;

    /// <summary>
    /// <paramref name="value"/> as Java's properties reader reads it where it was written that way, and
    /// as written otherwise. Java escapes the colon of every drive letter, so <c>\:</c> marks the form.
    /// A plain Windows path never holds it, and reading a plain path the Java way would drop every
    /// separator in it.
    /// </summary>
    internal static string Unescaped(string value)
    {
        if (!value.Contains(@"\:", StringComparison.Ordinal))
        {
            return value;
        }

        var unescaped = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
            }

            unescaped.Append(value[i]);
        }

        return unescaped.ToString();
    }
}
