#nullable enable

using System;
using System.IO;
using Microsoft.Build.Framework;

/// <summary>
/// Marks a 32-bit x86 Windows executable large-address-aware, so it is given 4 GB of address space on
/// 64-bit Windows rather than 2 GB. The SDK's x86 apphost does not carry the flag, nothing in the SDK
/// sets it, and a duplicate search of a large tree needs more than 2 GB (#709).
///
/// <para>Compiled by MSBuild's RoslynCodeTaskFactory from Directory.Build.targets, and by
/// Deguffer.Core.Tests, which tests <see cref="LargeAddressAwareImage"/>. So it depends on nothing
/// but the BCL and Microsoft.Build.Framework, and implements <see cref="ITask"/> rather than deriving
/// from the Utilities task base.</para>
/// </summary>
public sealed class MarkLargeAddressAware : ITask
{
    /// <summary>The executable to mark, changed in place.</summary>
    [Required]
    public string Executable { get; set; } = string.Empty;

    public IBuildEngine BuildEngine { get; set; } = null!;

    public ITaskHost HostObject { get; set; } = null!;

    public bool Execute()
    {
        try
        {
            using var image = new FileStream(Executable, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

            if (LargeAddressAwareImage.Mark(image))
            {
                BuildEngine.LogMessageEvent(new BuildMessageEventArgs(
                    $"Marked {Executable} large-address-aware.", null, nameof(MarkLargeAddressAware), MessageImportance.Normal));
            }

            return true;
        }

        // A file that is not an x86 image, or that cannot be opened, is a build that would ship a
        // 32-bit executable limited to 2 GB, so it fails rather than warns.
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            BuildEngine.LogErrorEvent(new BuildErrorEventArgs(
                null, "DEGUFFER0001", Executable, 0, 0, 0, 0,
                $"Could not mark {Executable} large-address-aware: {ex.Message}", null, nameof(MarkLargeAddressAware)));
            return false;
        }
    }
}

/// <summary>Reads and sets IMAGE_FILE_LARGE_ADDRESS_AWARE in a PE image's COFF header.</summary>
public static class LargeAddressAwareImage
{
    /// <summary>IMAGE_FILE_LARGE_ADDRESS_AWARE, in the COFF header's Characteristics.</summary>
    public const ushort LargeAddressAwareFlag = 0x0020;

    /// <summary>IMAGE_FILE_MACHINE_I386: the only machine whose images default to 2 GB on 64-bit Windows.</summary>
    public const ushort I386 = 0x014C;

    // The DOS header's e_lfanew, the offset of the PE signature.
    private const int PeOffsetField = 0x3C;

    // After the 4-byte signature: Machine (2 bytes), then Characteristics 18 bytes into the COFF header.
    private const int MachineAfterPe = 4;
    private const int CharacteristicsAfterPe = 4 + 18;

    /// <summary>
    /// Set the flag in <paramref name="image"/>, a seekable and writable PE image, writing nothing
    /// where it is set already. Only the two bytes of the Characteristics field change. The PE
    /// checksum is not updated: the apphost carries none (zero), and Windows checks it only for
    /// drivers and boot-time images.
    /// </summary>
    /// <returns>true if this call set the flag; false if it was set already.</returns>
    /// <exception cref="InvalidDataException">The image is not a PE image for an x86 processor.</exception>
    public static bool Mark(Stream image)
    {
        var characteristics = CharacteristicsOffset(image);
        var value = ReadUInt16(image, characteristics);

        if ((value & LargeAddressAwareFlag) != 0)
        {
            return false;
        }

        WriteUInt16(image, characteristics, (ushort)(value | LargeAddressAwareFlag));
        return true;
    }

    private static long CharacteristicsOffset(Stream image)
    {
        if (image.Length < PeOffsetField + 4 || ReadUInt16(image, 0) != 0x5A4D)
        {
            throw new InvalidDataException("The file is not a Windows executable: it has no MZ header.");
        }

        long pe = ReadUInt32(image, PeOffsetField);

        if (pe + CharacteristicsAfterPe + 2 > image.Length || ReadUInt32(image, pe) != 0x00004550)
        {
            throw new InvalidDataException("The file is not a Windows executable: it has no PE signature where its header says.");
        }

        var machine = ReadUInt16(image, pe + MachineAfterPe);

        if (machine != I386)
        {
            throw new InvalidDataException(
                $"The executable is for machine 0x{machine:X4}, not x86 (0x{I386:X4}), so the flag would change nothing.");
        }

        return pe + CharacteristicsAfterPe;
    }

    private static ushort ReadUInt16(Stream image, long offset)
    {
        var bytes = Read(image, offset, 2);
        return (ushort)(bytes[0] | bytes[1] << 8);
    }

    private static uint ReadUInt32(Stream image, long offset)
    {
        var bytes = Read(image, offset, 4);
        return (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16 | bytes[3] << 24);
    }

    private static byte[] Read(Stream image, long offset, int count)
    {
        var bytes = new byte[count];
        image.Position = offset;

        for (var read = 0; read < count;)
        {
            var got = image.Read(bytes, read, count - read);

            if (got == 0)
            {
                throw new InvalidDataException("The file ends inside its own header.");
            }

            read += got;
        }

        return bytes;
    }

    private static void WriteUInt16(Stream image, long offset, ushort value)
    {
        image.Position = offset;
        image.Write(new[] { (byte)value, (byte)(value >> 8) }, 0, 2);
        image.Flush();
    }
}
