using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The component store as each of Windows' cleanup commands last left it, when Deguffer ran the command
/// to a successful end, kept in <c>%LOCALAPPDATA%\Deguffer\component-store.json</c>.
///
/// <para><b>Why DISM's own verdict is not enough.</b> Its analysis can recommend a cleanup that frees
/// nothing, and go on recommending it after every run. On a Windows serviced by checkpoint cumulative
/// updates, Windows 11 24H2 among them, the checkpoint update is the base every later update is a
/// forward-only delta against. The analysis counts it, and the feature payloads Windows keeps beside it,
/// as reclaimable, and the cleanup keeps every byte: the servicing stack logs that the store "matches
/// last scavenge cookie, skipping scavenge". Without this record the row offered the same gigabytes at
/// every scan, and every clean freed nothing.</para>
///
/// <para><b>Evidence, not a prediction.</b> A command that ran to its end has already removed all it
/// will remove from the store it found. While DISM's analysis reads the same figures as it did straight
/// after that run, the store is the one the command left, and the command has nothing more to do. Any
/// change, such as an update installed or Windows' own scheduled cleanup, offers the row again.</para>
///
/// <para><b>A file that cannot be read is no record.</b> A torn write, a hand edit or a format this
/// build does not know all cost one more run of a cleanup that may free nothing. Reading part of one
/// could hide a cleanup that would free gigabytes.</para>
/// </summary>
public sealed class ComponentStoreSettlement
{
    /// <summary>The format this build writes and reads. A file of any other is not read.</summary>
    private const int Format = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    private readonly string _file;
    private readonly Lock _gate = new();
    private Dictionary<ComponentStoreCommand, ComponentStoreState>? _left;

    public ComponentStoreSettlement(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _file = Path.Combine(environment.LocalAppData, "Deguffer", "component-store.json");
    }

    /// <summary>
    /// Whether <paramref name="command"/> ran to a successful end on the store <paramref name="now"/>
    /// describes, so running it again frees nothing.
    /// </summary>
    public bool HasSettled(ComponentStoreCommand command, ComponentStoreState now)
    {
        lock (_gate)
        {
            return Left().TryGetValue(command, out var left) && left == now;
        }
    }

    /// <summary>
    /// Record that <paramref name="command"/> ran to a successful end and left the store as
    /// <paramref name="left"/> describes. The reset removes everything the cleanup removes, so it settles
    /// the cleanup as well. A record that cannot be written still answers for this session.
    /// </summary>
    public void Record(ComponentStoreCommand command, ComponentStoreState left)
    {
        lock (_gate)
        {
            var record = Left();

            foreach (var settled in Settles(command))
            {
                record[settled] = left;
            }

            try
            {
                var stored = new Stored(
                    Format,
                    record.ToDictionary(entry => entry.Key.ToString(), entry => StoredState.Of(entry.Value), StringComparer.Ordinal));

                StoreFile.Replace(_file, JsonSerializer.Serialize(stored, SerializerOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held or refused. The next session runs the command once more, which is the cost of
                // having no record.
            }
        }
    }

    private static ComponentStoreCommand[] Settles(ComponentStoreCommand command) => command switch
    {
        ComponentStoreCommand.ResetBase => [ComponentStoreCommand.ResetBase, ComponentStoreCommand.Cleanup],
        _ => [command],
    };

    /// <summary>The record, read from the file the first time it is asked for. Called inside the gate.</summary>
    private Dictionary<ComponentStoreCommand, ComponentStoreState> Left() => _left ??= Load();

    private Dictionary<ComponentStoreCommand, ComponentStoreState> Load()
    {
        Stored? stored;

        try
        {
            stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(LongPath.Extended(_file)), SerializerOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing before the first cleanup, refused, torn, or not this format at all.
            return [];
        }

        if (stored is not { Format: Format })
        {
            return [];
        }

        var left = new Dictionary<ComponentStoreCommand, ComponentStoreState>();

        foreach (var (name, state) in stored.Left)
        {
            // The annotations are not enforced on what a dictionary holds, so a null there is refused
            // here, as the rest of a file this store would not write is.
            if (!Enum.TryParse<ComponentStoreCommand>(name, ignoreCase: false, out var command)
                || !Enum.IsDefined(command)
                || name != command.ToString()
                || state?.ToState() is not { } read)
            {
                return [];
            }

            left[command] = read;
        }

        return left;
    }

    /// <summary>The file as it is written.</summary>
    private sealed record Stored(int Format, Dictionary<string, StoredState> Left);

    /// <summary>
    /// One state as it is written: every member a constructor parameter, so a state missing any of them
    /// is refused rather than read as a zero.
    /// </summary>
    private sealed record StoredState(
        long ExplorerSize,
        long ActualSize,
        long SharedWithWindows,
        long BackupsAndDisabledFeatures,
        long CacheAndTemporaryData,
        int ReclaimablePackages)
    {
        public static StoredState Of(ComponentStoreState state) =>
            new(
                state.ExplorerSize,
                state.ActualSize,
                state.SharedWithWindows,
                state.BackupsAndDisabledFeatures,
                state.CacheAndTemporaryData,
                state.ReclaimablePackages);

        /// <summary>The state, or null where a figure is one DISM never reports.</summary>
        public ComponentStoreState? ToState() =>
            ExplorerSize >= 0 && ActualSize >= 0 && SharedWithWindows >= 0 && BackupsAndDisabledFeatures >= 0
            && CacheAndTemporaryData >= 0 && ReclaimablePackages >= 0
                ? new ComponentStoreState(
                    ExplorerSize, ActualSize, SharedWithWindows, BackupsAndDisabledFeatures, CacheAndTemporaryData, ReclaimablePackages)
                : null;
    }
}

/// <summary>Which of Windows' component store cleanups a row runs.</summary>
public enum ComponentStoreCommand
{
    /// <summary><c>/StartComponentCleanup</c>.</summary>
    Cleanup,

    /// <summary><c>/StartComponentCleanup /ResetBase</c>, which removes everything the cleanup does and more.</summary>
    ResetBase,
}

/// <summary>
/// The figures of DISM's analysis that describe the store itself, which change whenever anything is
/// added to the store or removed from it. The date of the last cleanup is left out: Windows' scheduled
/// cleanup moves it without changing the store.
/// </summary>
public sealed record ComponentStoreState(
    long ExplorerSize,
    long ActualSize,
    long SharedWithWindows,
    long BackupsAndDisabledFeatures,
    long CacheAndTemporaryData,
    int ReclaimablePackages);
