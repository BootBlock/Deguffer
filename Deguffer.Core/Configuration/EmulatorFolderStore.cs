using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Configuration;

/// <summary>
/// The folders the user has told Deguffer an emulator is installed in, persisted as a JSON array of
/// paths beside the other settings.
///
/// <para><b>Declared, because nothing else can say.</b> RPCS3 keeps everything beside its own
/// executable wherever the archive was unpacked, and the portable layouts of Cemu, Dolphin and PCSX2
/// do the same, as does every RetroArch. Windows records none of those places anywhere Deguffer may
/// rely on, so §5.2's "never assume a location" leaves the user as the only source. A folder listed
/// here is never trusted on its own word: an emulator's root inside it is recognised only by the file
/// that emulator writes there, see <see cref="Providers.EmulatorLayout"/>, and RetroArch only by its
/// program, see <see cref="Providers.RetroArchDiscovery"/>.</para>
/// </summary>
public sealed class EmulatorFolderStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly string _file;

    public EmulatorFolderStore(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _directory = Path.Combine(environment.LocalAppData, "Deguffer");
        _file = Path.Combine(_directory, "emulator-folders.json");
    }

    public IReadOnlyList<string> Load()
    {
        // Missing until the user declares a folder, which most never do, and read by every emulator
        // provider on every scan. Asked first so that the ordinary case costs no exception; the catch
        // below still answers for the file going between the two.
        if (LongPath.ProbeFile(_file) is PathPresence.Absent)
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(LongPath.Extended(_file));

            return Usable(JsonSerializer.Deserialize<string?[]>(json, SerializerOptions) ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing on first run, unreadable, or corrupt. All three mean no folder is declared,
            // which reaches less rather than guessing at more.
            return [];
        }
    }

    /// <summary>
    /// Write <paramref name="folders"/>, and report what was actually kept: a blank or relative
    /// entry is dropped, and a folder listed twice is kept once, in the place it was first listed.
    /// </summary>
    public bool Save(IReadOnlyList<string> folders, out IReadOnlyList<string> stored)
    {
        ArgumentNullException.ThrowIfNull(folders);

        stored = Usable(folders);

        try
        {
            Directory.CreateDirectory(LongPath.Extended(_directory));
            File.WriteAllText(LongPath.Extended(_file), JsonSerializer.Serialize(stored, SerializerOptions));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stored = [];
            return false;
        }
    }

    public bool Save(IReadOnlyList<string> folders) => Save(folders, out _);

    private static IReadOnlyList<string> Usable(IEnumerable<string?> folders) =>
    [
        .. folders
            .Select(LongPath.Entry)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];
}
