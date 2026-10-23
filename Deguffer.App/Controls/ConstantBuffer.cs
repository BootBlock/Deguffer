using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Deguffer.App.Controls;

/// <summary>
/// A shader's constant buffer declared from the C# struct that fills it, so the two cannot disagree
/// about which value is where: the HLSL members come from the struct's fields, in the order the struct
/// lays them out, under the fields' own names.
///
/// <para>HLSL packs a constant buffer in 16-byte registers, and starts a member on the next register
/// where it would cross one; C# packs a sequential struct at four bytes. Where the two would place a
/// field apart, the declaration is refused with the field named, rather than made: every value after
/// it would be read from the wrong place, and the picture scrambled without an error.</para>
/// </summary>
internal static class ConstantBuffer
{
    /// <summary>The size of one of HLSL's constant registers, in bytes.</summary>
    private const int Register = 16;

    /// <summary>
    /// The members of a <c>cbuffer</c> laid out as <typeparamref name="T"/> is, for a macro that
    /// stands in the shader's <c>cbuffer</c> block.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A field has a type HLSL has no match for here, or HLSL would place it elsewhere than C# does.
    /// </exception>
    public static string Members<T>()
        where T : unmanaged
    {
        var members = new StringBuilder();
        var packed = 0;

        foreach (var (field, offset) in Fields<T>())
        {
            var (type, size) = Hlsl(field);

            if ((packed % Register) + size > Register)
            {
                packed += Register - (packed % Register);
            }

            if (offset != packed)
            {
                throw new InvalidOperationException(
                    $"{typeof(T).Name}.{field.Name} is at byte {offset}, where HLSL would read it from byte {packed}.");
            }

            members.Append(type).Append(' ').Append(field.Name).Append("; ");
            packed += size;
        }

        return members.ToString().TrimEnd();
    }

    /// <summary>
    /// How many bytes a constant buffer of <typeparamref name="T"/> takes: the struct, made up to a
    /// whole register, which is the least size Direct3D accepts for one.
    /// </summary>
    public static int SizeOf<T>()
        where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();

        return size + ((Register - (size % Register)) % Register);
    }

    private static IEnumerable<(FieldInfo Field, int Offset)> Fields<T>() =>
        typeof(T)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => (field, (int)Marshal.OffsetOf<T>(field.Name)))
            .OrderBy(entry => entry.Item2);

    private static (string Type, int Size) Hlsl(FieldInfo field) => field.FieldType switch
    {
        var type when type == typeof(float) => ("float", sizeof(float)),
        var type when type == typeof(int) => ("int", sizeof(int)),
        var type when type == typeof(uint) => ("uint", sizeof(uint)),
        var type when type == typeof(Vector2) => ("float2", 2 * sizeof(float)),
        var type when type == typeof(Vector3) => ("float3", 3 * sizeof(float)),
        var type when type == typeof(Vector4) => ("float4", 4 * sizeof(float)),
        var type => throw new InvalidOperationException(
            $"{field.DeclaringType?.Name}.{field.Name} is a {type.Name}, which a constant buffer here does not hold."),
    };
}
