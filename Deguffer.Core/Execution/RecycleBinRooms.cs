using System.Buffers.Binary;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>What one volume's Recycle Bin holds for this account, and how much it may hold.</summary>
/// <param name="Held">The length of everything in it now, in bytes.</param>
/// <param name="Limit">The most it may hold, in bytes, or null where the setting could not be read.</param>
/// <param name="KeepsNothing">
/// Whether Windows is set to delete what is sent to this bin rather than keep it ("Don't move files to
/// the Recycle Bin").
/// </param>
public sealed record RecycleBinRoom(long Held, long? Limit, bool KeepsNothing)
{
    /// <summary>How much more it can take before Windows deletes its oldest items, or null where that is not known.</summary>
    public long? Free => KeepsNothing ? 0 : Limit is { } limit ? Math.Max(0, limit - Held) : null;
}

/// <summary>
/// The room in each volume's Recycle Bin (§7.4): when a bin is full, Windows deletes its oldest items
/// outright to make room for new ones, and those can include copies sent there earlier in the same
/// removal, so the confirmation says where the copies bound for one bin are more than it can take.
///
/// <para><b>Measured on 2026-10-09.</b> <c>SHQueryRecycleBin</c> answers what this account's bin on a
/// volume holds as the sum of its items' lengths (a 3,145,733-byte file sent there read as exactly
/// that), and takes a drive's top, a folder on it, the extended form of either, and the volume's
/// <c>\\?\Volume{GUID}\</c> name alike, all four reading the same bin. The limit is
/// the <c>MaxCapacity</c> number, in megabytes, under the account's
/// <c>Explorer\BitBucket\Volume\{GUID}</c> key for the volume, where the GUID is the one in the
/// volume's <c>\\?\Volume{GUID}\</c> name; <c>NukeOnDelete</c> beside it is the setting that keeps
/// nothing. On the machine measured, every fixed volume had both values, with limits from 4% to 52% of
/// the volume.</para>
///
/// <para>Stateless, so one instance serves the process (G5).</para>
/// </summary>
public sealed class RecycleBinRooms
{
    private const string VolumeKeys = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume";

    private const long Megabyte = 1024 * 1024;

    public static RecycleBinRooms Default { get; } = new(Query, UserEnvironment.Current);

    private readonly Func<string, long?> _held;
    private readonly IUserEnvironment _environment;

    /// <param name="held">
    /// What the bin of a volume holds, given the volume's name or its top, or null where Windows would not say. A seam,
    /// so a test can stand for a bin of any size without filling the real one.
    /// </param>
    /// <param name="environment">Where the bin's settings are read, under the account's own key.</param>
    internal RecycleBinRooms(Func<string, long?> held, IUserEnvironment environment)
    {
        _held = held;
        _environment = environment;
    }

    /// <summary>The room in <paramref name="volume"/>'s bin, or null where Windows would not say what it holds.</summary>
    public RecycleBinRoom? Of(LocalVolume volume)
    {
        // By the volume's own name where it has one, because a folder a volume is mounted at is a
        // folder on another volume, and names that volume's bin.
        if (_held(volume.VolumeName ?? volume.RootPath) is not { } held)
        {
            return null;
        }

        if (VolumeGuid(volume.VolumeName) is not { } guid)
        {
            return new RecycleBinRoom(held, Limit: null, KeepsNothing: false);
        }

        var key = $@"{VolumeKeys}\{guid}";
        var limit = _environment.ReadCurrentUserRegistryNumber(key, "MaxCapacity") is { } megabytes and >= 0
            ? megabytes * Megabyte
            : (long?)null;

        return new RecycleBinRoom(held, limit, _environment.ReadCurrentUserRegistryNumber(key, "NukeOnDelete") is 1);
    }

    /// <summary>The <c>{GUID}</c> of a <c>\\?\Volume{GUID}\</c> name, or null where there is none.</summary>
    private static string? VolumeGuid(string? volumeName)
    {
        if (volumeName is null)
        {
            return null;
        }

        var open = volumeName.IndexOf('{', StringComparison.Ordinal);
        var close = volumeName.IndexOf('}', StringComparison.Ordinal);

        return open >= 0 && close > open ? volumeName[open..(close + 1)] : null;
    }

    private static long? Query(string root)
    {
        // See ShellNative.SHQueryRecycleBin for the two layouts.
        var packed = IntPtr.Size == 4;
        var info = new byte[packed ? 20 : 24];
        BinaryPrimitives.WriteInt32LittleEndian(info, info.Length);

        return ShellNative.SHQueryRecycleBin(root, info) == 0
            ? BinaryPrimitives.ReadInt64LittleEndian(info.AsSpan(packed ? 4 : 8))
            : null;
    }
}
