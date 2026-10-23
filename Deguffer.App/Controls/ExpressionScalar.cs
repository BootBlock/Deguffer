using System.Globalization;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.App.Controls;

/// <summary>
/// A number in a compositor expression: its text, which a rule in Core worked over
/// <see cref="IScalar{T}"/> builds up as it would work a value out. So the camera's expressions are
/// Core's rules themselves, and what the screen shows and what a click is resolved through cannot
/// drift apart (§7.1).
///
/// <para>Every operation is bracketed, so the expression keeps the rule's order whatever the
/// compositor's precedence.</para>
/// </summary>
/// <param name="Text">The expression, such as <c>camera.Scale.X</c>.</param>
internal readonly record struct ExpressionScalar(string Text) : IScalar<ExpressionScalar>
{
    /// <summary>A constant, in full and with a point whatever the culture, and never in an exponent, which an expression does not read.</summary>
    public static ExpressionScalar Of(double value) => new(value.ToString("0.0################", CultureInfo.InvariantCulture));

    public static ExpressionScalar Min(ExpressionScalar left, ExpressionScalar right) => new($"Min({left}, {right})");

    public static ExpressionScalar Max(ExpressionScalar left, ExpressionScalar right) => new($"Max({left}, {right})");

    public static ExpressionScalar Clamp(ExpressionScalar value, ExpressionScalar least, ExpressionScalar most) =>
        new($"Clamp({value}, {least}, {most})");

    public static ExpressionScalar operator +(ExpressionScalar left, ExpressionScalar right) => new($"({left} + {right})");

    public static ExpressionScalar operator -(ExpressionScalar left, ExpressionScalar right) => new($"({left} - {right})");

    public static ExpressionScalar operator *(ExpressionScalar left, ExpressionScalar right) => new($"({left} * {right})");

    public static ExpressionScalar operator /(ExpressionScalar left, ExpressionScalar right) => new($"({left} / {right})");

    public static ExpressionScalar operator -(ExpressionScalar value) => new($"(-{value})");

    public override string ToString() => Text;
}
