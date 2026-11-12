namespace Deguffer.Core.Tests;

/// <summary>
/// The build step that marks an x86 executable large-address-aware (#709), over images laid out as
/// the SDK's apphost is: a DOS header whose e_lfanew points past it to the PE signature and the COFF
/// header. The x86 apphost's Characteristics are 0x0102, without the flag. The release workflow reads
/// the flag from the published executable itself.
/// </summary>
public sealed class LargeAddressAwareImageTests
{
    private const ushort Amd64 = 0x8664;
    private const ushort X86ApphostCharacteristics = 0x0102;
    private const int PeAt = 0x80;
    private const int CharacteristicsAt = PeAt + 4 + 18;

    [Fact]
    public void AnUnmarkedX86ImageIsMarkedAndNothingElseChanges()
    {
        var original = Image(LargeAddressAwareImage.I386, X86ApphostCharacteristics);
        using var image = Writable(original);

        Assert.True(LargeAddressAwareImage.Mark(image));

        var marked = image.ToArray();
        Assert.Equal(0x0122, BitConverter.ToUInt16(marked, CharacteristicsAt));

        marked[CharacteristicsAt] = original[CharacteristicsAt];
        Assert.Equal(original, marked);
    }

    /// <summary>A build whose apphost was not recreated finds it marked, and changes nothing.</summary>
    [Fact]
    public void AMarkedImageIsLeftAsItIs()
    {
        var original = Image(LargeAddressAwareImage.I386, X86ApphostCharacteristics | LargeAddressAwareImage.LargeAddressAwareFlag);
        using var image = Writable(original);

        Assert.False(LargeAddressAwareImage.Mark(image));
        Assert.Equal(original, image.ToArray());
    }

    /// <summary>
    /// The flag changes nothing for a 64-bit image, so being handed one means the step was wired to the
    /// wrong build, and it says so rather than passing.
    /// </summary>
    [Fact]
    public void AnImageForAnotherMachineIsRefusedUnchanged()
    {
        // Unmarked, so a step that ignored the machine would write to it.
        var original = Image(Amd64, 0x0002);
        using var image = Writable(original);

        var refused = Assert.Throws<InvalidDataException>(() => LargeAddressAwareImage.Mark(image));

        Assert.Contains("0x8664", refused.Message);
        Assert.Equal(original, image.ToArray());
    }

    /// <summary>Each fault with the check that must refuse it, so a check removed is not covered by a later read failing.</summary>
    public static TheoryData<string, string> NotExecutables => new()
    {
        { "no MZ", "no MZ header" },
        { "no PE signature", "no PE signature" },
        { "PE offset past the end", "no PE signature" },
        { "too short", "no MZ header" },
    };

    [Theory]
    [MemberData(nameof(NotExecutables))]
    public void AFileThatIsNotAnExecutableIsRefusedUnchanged(string fault, string refusal)
    {
        var original = Image(LargeAddressAwareImage.I386, X86ApphostCharacteristics);
        original = fault switch
        {
            "no MZ" => With(original, 0, 0),
            "no PE signature" => With(original, PeAt, (byte)'X'),
            "PE offset past the end" => With(original, 0x3D, 0x10),
            _ => original[..0x30],
        };
        using var image = Writable(original);

        var refused = Assert.Throws<InvalidDataException>(() => LargeAddressAwareImage.Mark(image));

        Assert.Contains(refusal, refused.Message, StringComparison.Ordinal);
        Assert.Equal(original, image.ToArray());
    }

    private static byte[] Image(ushort machine, int characteristics)
    {
        var image = new byte[0x200];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BitConverter.GetBytes(PeAt).CopyTo(image, 0x3C);
        "PE\0\0"u8.ToArray().CopyTo(image, PeAt);
        BitConverter.GetBytes(machine).CopyTo(image, PeAt + 4);
        BitConverter.GetBytes((ushort)characteristics).CopyTo(image, CharacteristicsAt);

        // The bytes around the field are not zero, so a write that strays from it is seen.
        for (var at = PeAt + 6; at < image.Length; at++)
        {
            if (at is not (CharacteristicsAt or CharacteristicsAt + 1))
            {
                image[at] = (byte)at;
            }
        }

        return image;
    }

    private static byte[] With(byte[] image, int at, byte value)
    {
        var changed = (byte[])image.Clone();
        changed[at] = value;
        return changed;
    }

    private static MemoryStream Writable(byte[] image)
    {
        var stream = new MemoryStream();
        stream.Write(image);
        return stream;
    }
}
