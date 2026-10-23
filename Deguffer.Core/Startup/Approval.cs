using System.Buffers.Binary;
using Microsoft.Win32;

namespace Deguffer.Core.Startup;

/// <summary>Whether Windows will start an entry at sign-in.</summary>
public enum StartupState
{
    /// <summary>
    /// What Windows recorded could not be read as either answer. The zero value on purpose, so a state
    /// nobody set reads as the one that claims nothing.
    /// </summary>
    Unknown,

    Starts,

    Disabled,
}

/// <summary>
/// An entry's <c>StartupApproved</c> value, read on the footing §7.5 measured: 12 bytes of
/// <c>REG_BINARY</c>, a four-byte state and the FILETIME at which the entry was disabled.
///
/// <para><b>None of this is documented.</b> The layout and the meaning of the state come from
/// [startup-apps.md](../../docs/todo/startup-apps.md): Settings wrote 1 to disable and 0 to enable,
/// Task Manager wrote 3 and 2, and Task Manager set the 2 bit on a value it read, keeping the time. So
/// the low bit is "disabled", which is also how Task Manager showed the one value with the 4 bit set.
/// A value that is not 12 bytes of binary says nothing at all. A value whose state has a bit beyond
/// those two is read by its low bit and never replaced, because nobody has explained what the other
/// bit records.</para>
/// </summary>
public sealed record Approval
{
    /// <summary>The length every measured value had.</summary>
    public const int Length = 12;

    /// <summary>The state Task Manager writes to enable an entry.</summary>
    private const uint TaskManagerStarts = 2;

    /// <summary>The state Task Manager writes to disable an entry.</summary>
    private const uint TaskManagerDisabled = 3;

    /// <summary>Every bit a measured state was seen to use: disabled, and Task Manager's mark.</summary>
    private const uint ExplainedBits = 0x3;

    /// <summary>No value, which every source measured reads as an entry that starts.</summary>
    public static Approval None { get; } = new(null, readable: true);

    /// <summary>
    /// Windows would not open the key the value would be in. Not <see cref="None"/>: a key nobody
    /// could read is not a key that holds nothing, and reading it as one would report a disabled entry
    /// as starting.
    /// </summary>
    public static Approval Unreadable { get; } = new(null, readable: false);

    private readonly bool _readable;

    private Approval(StoredValue? value, bool readable)
    {
        Value = value;
        _readable = readable;
    }

    /// <summary>The value as stored, or null where there is none.</summary>
    public StoredValue? Value { get; }

    /// <summary>Wrap what the registry holds for an entry, or null where it holds nothing.</summary>
    public static Approval Of(StoredValue? value) => value is null ? None : new Approval(value, readable: true);

    /// <summary>
    /// The four-byte state, or null where the value is not 12 bytes of binary and so has no state
    /// Deguffer can read.
    /// </summary>
    public uint? Code => Value is null ? null
        : Value.Bytes is { Length: Length } bytes ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : null;

    /// <summary>Whether the entry starts: no value says it does, and the low bit of the state says it does not.</summary>
    public StartupState State => !_readable ? StartupState.Unknown
        : Value is null ? StartupState.Starts
        : Code is not { } code ? StartupState.Unknown
        : (code & 1) == 0 ? StartupState.Starts
        : StartupState.Disabled;

    /// <summary>
    /// When the entry was disabled, where the value says it is and records a time. Windows writes a
    /// zero time for an entry that starts.
    /// </summary>
    public DateTime? DisabledAt =>
        State is StartupState.Disabled && Value?.Bytes is { } bytes
        && BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(4)) is > 0 and var time
            ? DateTime.FromFileTimeUtc(time)
            : null;

    /// <summary>
    /// Whether Deguffer may replace this value: there is none, or its every bit is one the
    /// measurement explained (§7.5). Replacing anything else would throw away what it says.
    /// </summary>
    public bool IsReplaceable => _readable && (Value is null || Code is { } code && (code & ~ExplainedBits) == 0);

    /// <summary>The value Task Manager writes to let an entry start: the state 2 and a zero time.</summary>
    public static StoredValue Starting() => Bytes(TaskManagerStarts, 0);

    /// <summary>The value Task Manager writes to stop an entry starting: the state 3 and the time it was done.</summary>
    public static StoredValue DisabledOn(DateTimeOffset now) => Bytes(TaskManagerDisabled, now.UtcDateTime.ToFileTimeUtc());

    private static StoredValue Bytes(uint state, long time)
    {
        var bytes = new byte[Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, state);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), time);

        return new StoredValue(RegistryValueKind.Binary, bytes);
    }
}
