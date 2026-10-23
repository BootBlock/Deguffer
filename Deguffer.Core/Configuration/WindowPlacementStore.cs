using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Configuration;

/// <summary>A window's remembered placement, which a store reads back only where it describes a window at all.</summary>
public interface IWindowPlacement
{
    /// <summary>Whether this describes a window at all. See <see cref="WindowMetrics.IsUsable"/>.</summary>
    bool IsUsable { get; }
}

/// <summary>
/// Reads and writes one window's placement as JSON under <c>%LOCALAPPDATA%\Deguffer</c>: the main
/// window's <see cref="WindowMetrics"/>, and a tool window's <see cref="ToolWindowMetrics"/>, each in a
/// file of its own.
///
/// In Core rather than the shell for the reason the other stores are: what is worth testing is a file
/// that is missing, truncated or hand-edited into nonsense, and none of that is reachable through a
/// WinUI window.
///
/// Every failure degrades to remembering nothing, which is the framework-default placement the app
/// shipped with. A placement is not worth failing a launch over, and it is the one stored value
/// whose loss costs the user a drag of the mouse.
/// </summary>
/// <typeparam name="T">The placement the file holds.</typeparam>
public sealed class WindowPlacementStore<T>
    where T : class, IWindowPlacement
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _file;

    /// <param name="fileName">The file under <c>%LOCALAPPDATA%\Deguffer</c> this window's placement is kept in.</param>
    public WindowPlacementStore(IUserEnvironment environment, string fileName)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        _file = Path.Combine(environment.LocalAppData, "Deguffer", fileName);
    }

    /// <summary>
    /// Where the window was left, or null where the file is missing, unreadable, corrupt, or
    /// describes something that is not a window.
    /// </summary>
    public T? Load()
    {
        try
        {
            var json = File.ReadAllText(LongPath.Extended(_file));
            var stored = JsonSerializer.Deserialize<T>(json, SerializerOptions);

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
    /// Persist <paramref name="placement"/>. Returns whether it was written — nothing tells the user
    /// about this file, so the answer is for a caller that wants to stop trying rather than for a
    /// message.
    /// </summary>
    public bool Save(T placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        try
        {
            StoreFile.Replace(_file, JsonSerializer.Serialize(placement, SerializerOptions));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
