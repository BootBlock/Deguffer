using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The identifier both sweeps recognise their own scratch by. It is what stands between a sweep and
/// somebody else's directory or key, so the cases that matter here are the refusals.
/// </summary>
public sealed class ScratchTests
{
    /// <summary>The recogniser accepts what the suite writes, asked of the writer rather than of a literal.</summary>
    [Fact]
    public void RecognisesWhatItWrites() => Assert.True(Scratch.IsIdentifier(Scratch.NewIdentifier()));

    /// <summary>
    /// Names the recogniser has to refuse. The first two are what a shape test alone would let
    /// through: <see cref="Guid.TryParseExact(string, string, out Guid)"/> trims its input and
    /// accepts either case, and <see cref="Scratch.NewIdentifier"/> writes neither.
    /// </summary>
    [Theory]
    [InlineData(" 0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("notes")]
    [InlineData("")]
    public void RefusesANameNoScratchWouldCarry(string name) => Assert.False(Scratch.IsIdentifier(name));
}
