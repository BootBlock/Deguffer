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

    /// <summary>Resolve an executable on <c>PATH</c>, or null if it is not installed.</summary>
    string? FindExecutable(string command);
}

/// <inheritdoc />
public sealed class UserEnvironment : IUserEnvironment
{
    public static readonly UserEnvironment Current = new();

    public string UserProfile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string LocalAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string RoamingAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string? FindExecutable(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in directories)
        {
            foreach (var candidate in Candidates(directory, command, extensions))
            {
                if (LongPath.FileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string directory, string command, string[] extensions)
    {
        // A bad PATH entry is normal on a developer machine; skip it rather than failing the scan.
        string? baseName;
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

        foreach (var extension in extensions)
        {
            yield return baseName + extension;
        }
    }
}
