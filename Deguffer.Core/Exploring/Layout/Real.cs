namespace Deguffer.Core.Exploring.Layout;

/// <summary>A number worked out now, in double precision.</summary>
public readonly record struct Real(double Value) : IScalar<Real>
{
    public static Real Of(double value) => new(value);

    public static Real Min(Real left, Real right) => new(Math.Min(left.Value, right.Value));

    public static Real Max(Real left, Real right) => new(Math.Max(left.Value, right.Value));

    public static Real Clamp(Real value, Real least, Real most) => new(Math.Clamp(value.Value, least.Value, most.Value));

    public static implicit operator Real(double value) => new(value);

    public static Real operator +(Real left, Real right) => new(left.Value + right.Value);

    public static Real operator -(Real left, Real right) => new(left.Value - right.Value);

    public static Real operator *(Real left, Real right) => new(left.Value * right.Value);

    public static Real operator /(Real left, Real right) => new(left.Value / right.Value);

    public static Real operator -(Real value) => new(-value.Value);
}
