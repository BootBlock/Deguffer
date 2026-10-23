using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Configuration;

/// <summary>
/// Reads and writes <see cref="WindowMetrics"/> as JSON under <c>%LOCALAPPDATA%\Deguffer</c>.
///
/// In Core rather than the shell for the reason the other three stores are: what is worth testing
/// is a file that is missing, truncated or hand-edited into nonsense, and none of that is reachable
/// through a WinUI window.
///
/// Every failure degrades to remembering nothing, which is the framework-default placement the app
/// shipped with. A placement is not worth failing a launch over, and it is the one stored value
/// whose loss costs the user a drag of the mouse.
/// </summary>
public sealed class WindowMetricsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly string _file;

    public WindowMetricsStore(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _directory = Path.Combine(environment.LocalAppData, "Deguffer");
        _file = Path.Combine(_directory, "window.json");
    }

    /// <summary>
    /// Where the window was left, or null where the file is missing, unreadable, corrupt, or
    /// describes something that is not a window.
    /// </summary>
    public WindowMetrics? Load()
    {
        try
        {
            var json = File.ReadAllText(LongPath.Extended(_file));
            var stored = JsonSerializer.Deserialize<WindowMetrics>(json, SerializerOptions);

            // `null` and `{}` are both well-formed documents, and neither reaches the catch: the
            // first arrives as no record, the second as a zero-sized one.
            return stored is { IsUsable: true } ? stored : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing on first run, unreadable, or hand-edited into nonsense.
            return null;
        }
    }

    /// <summary>
    /// Persist <paramref name="metrics"/>. Returns whether it was written — nothing tells the user
    /// about this file, so the answer is for a caller that wants to stop trying rather than for a
    /// message.
    /// </summary>
    public bool Save(WindowMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        try
        {
            Directory.CreateDirectory(LongPath.Extended(_directory));
            File.WriteAllText(LongPath.Extended(_file), JsonSerializer.Serialize(metrics, SerializerOptions));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
