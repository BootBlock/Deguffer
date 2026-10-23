using System.Numerics;
using System.Runtime.InteropServices;
using Deguffer.App.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// A shader's constant buffer is declared from the struct that fills it, and a struct HLSL would read
/// differently from how C# writes it is refused rather than declared: every value after the first one
/// out of place would be read from the wrong bytes, which scrambles the picture with no error.
/// </summary>
public sealed class ConstantBufferTests
{
    /// <summary>The members are the struct's fields, in its order, under their own names and HLSL's types.</summary>
    [Fact]
    public void TheMembersAreTheStructsFieldsInItsOrder() =>
        Assert.Equal("float2 Size; float Reach; uint Shading; float3 Tone; int Count;", ConstantBuffer.Members<Packed>());

    /// <summary>
    /// A float4 after a lone float: C# puts it four bytes in, and HLSL on the next register, sixteen
    /// bytes in, because a member never crosses one.
    /// </summary>
    [Fact]
    public void AFieldHlslWouldStartOnTheNextRegisterIsRefused()
    {
        var refusal = Assert.Throws<InvalidOperationException>(ConstantBuffer.Members<Straddling>);

        Assert.Contains(nameof(Straddling.Region), refusal.Message);
    }

    /// <summary>A gap C# leaves that HLSL would not: HLSL packs the next member straight after the last.</summary>
    [Fact]
    public void AFieldAfterAGapHlslWouldCloseIsRefused()
    {
        var refusal = Assert.Throws<InvalidOperationException>(ConstantBuffer.Members<Gapped>);

        Assert.Contains(nameof(Gapped.After), refusal.Message);
    }

    [Fact]
    public void AFieldOfATypeWithNoHlslMatchIsRefused()
    {
        var refusal = Assert.Throws<InvalidOperationException>(ConstantBuffer.Members<Unmatched>);

        Assert.Contains(nameof(Unmatched.Precise), refusal.Message);
    }

    /// <summary>Direct3D takes a constant buffer only in whole registers, so the struct is made up to one.</summary>
    [Fact]
    public void TheBufferIsTheStructMadeUpToAWholeRegister()
    {
        Assert.Equal(32, ConstantBuffer.SizeOf<Straddling>());
        Assert.Equal(32, ConstantBuffer.SizeOf<Packed>());
    }

    // Only the layouts are read, never a value.
#pragma warning disable CS0649
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Packed
    {
        public readonly Vector2 Size;
        public readonly float Reach;
        public readonly uint Shading;
        public readonly Vector3 Tone;
        public readonly int Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Straddling
    {
        public readonly float Reach;
        public readonly Vector4 Region;
    }

    [StructLayout(LayoutKind.Explicit)]
    private readonly struct Gapped
    {
        [FieldOffset(0)]
        public readonly float Before;

        [FieldOffset(8)]
        public readonly float After;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Unmatched
    {
        public readonly double Precise;
    }
#pragma warning restore CS0649
}
