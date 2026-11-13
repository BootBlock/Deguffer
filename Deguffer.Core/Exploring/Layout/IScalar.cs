using System.Numerics;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// A number a map's camera rule can be worked in: a value now (<see cref="Real"/>), or an expression the
/// compositor works out at every frame (the shell's own). A rule written once over this is the one rule
/// for both, so what the screen shows and what a click is resolved through cannot drift apart (§7.1).
///
/// <para>Only the arithmetic those rules use: the four operations, negation, and the three the
/// compositor's expressions name the same way.</para>
/// </summary>
public interface IScalar<T> :
    IAdditionOperators<T, T, T>,
    ISubtractionOperators<T, T, T>,
    IMultiplyOperators<T, T, T>,
    IDivisionOperators<T, T, T>,
    IUnaryNegationOperators<T, T>
    where T : IScalar<T>
{
    /// <summary>The constant <paramref name="value"/>.</summary>
    static abstract T Of(double value);

    static abstract T Min(T left, T right);

    static abstract T Max(T left, T right);

    /// <summary><paramref name="value"/> held between <paramref name="least"/> and <paramref name="most"/>.</summary>
    static abstract T Clamp(T value, T least, T most);
}
