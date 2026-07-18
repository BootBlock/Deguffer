using System.Collections.Concurrent;

namespace Deguffer.Core.Safety;

/// <summary>
/// The ambient machine, behind an interface so provider rules are testable against a temp
/// directory rather than the developer's real profile.
/// </summary>
public interface IUserEnvironment
{
    /// <summary><c>%USERPROFILE%</c>.</summary>
    string UserProfile { get; }

    /// <summary><c>%LOCALAPPDATA%</c>.</summary>
    string LocalAppData { get; }

    /// <summary><c>%APPDATA%</c>.</summary>
    string RoamingAppData { get; }

    /// <summary>The per-user temp directory — NuGet keeps <c>NuGetScratch</c> here.</summary>
    string TempPath { get; }

    /// <summary>Resolve an executable on <c>PATH</c>, or null if it is not installed.</summary>
    string? FindExecutable(string command);

    /// <summary>
    /// Discard cached lookups. Called at the start of a planning pass so a toolchain installed
    /// while the app was open is picked up on the next preview.
    /// </summary>
    void Invalidate();
}

/// <inheritdoc />
public sealed class UserEnvironment : IUserEnvironment
{
    public static readonly UserEnvironment Current = new();

    private static readonly string[] PathExtensions =
        (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly string[] PathDirectories =
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Resolving a command probes the filesystem across every PATH directory, and both
    // IsPresentAsync and PlanAsync ask for the same tools. Memoised for the life of a planning
    // pass — including negative results, which is why Invalidate exists: without it, a toolchain
    // installed while the app is open would stay invisible for the rest of the session.
    private readonly ConcurrentDictionary<string, string?> _resolved = new(StringComparer.OrdinalIgnoreCase);

    public string UserProfile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string LocalAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string RoamingAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string TempPath { get; } = Path.GetTempPath();

    public void Invalidate() => _resolved.Clear();

    public string? FindExecutable(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        return _resolved.GetOrAdd(command, static name =>
        {
            foreach (var directory in PathDirectories)
            {
                foreach (var candidate in Candidates(directory, name))
                {
                    if (LongPath.FileExists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        });
    }

    private static IEnumerable<string> Candidates(string directory, string command)
    {
        // A malformed PATH entry is normal on a developer machine; skip it rather than failing
        // the whole scan.
        string baseName;
        try
        {
            baseName = Path.Combine(directory, command);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        if (Path.HasExtension(command))
        {
            yield return baseName;
            yield break;
        }

        foreach (var extension in PathExtensions)
        {
            yield return baseName + extension;
        }
    }
}
