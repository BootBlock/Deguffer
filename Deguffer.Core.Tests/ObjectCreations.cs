using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Deguffer.Core.Tests;

/// <summary>
/// The types of an assembly whose code creates an instance of a given type, read from the IL rather
/// than the source, so a creation is found however the source spells it.
///
/// <para><b>A creation belongs to the type that wrote it.</b> The compiler moves the body of a lambda,
/// a local function, an iterator or an async method into a type it generates, nested in the one the
/// code was written in. Such a type is answered as the type it is nested in, so a test class that makes
/// an object only inside an <c>async</c> test is still the class found.</para>
/// </summary>
internal static class ObjectCreations
{
    private const BindingFlags Declared =
        BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private const byte TwoByteOpCodePrefix = 0xFE;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => opCode.Value);

    /// <summary>Every type in <paramref name="assembly"/> that creates a <paramref name="created"/>, in name order.</summary>
    public static IReadOnlyList<Type> TypesCreating(Assembly assembly, Type created) =>
        [.. assembly.GetTypes()
            .Where(type => type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)).Any(method => Creates(method, created)))
            .Select(Author)
            .Distinct()
            .OrderBy(type => type.FullName, StringComparer.Ordinal)];

    /// <summary>The type whose source holds <paramref name="type"/>'s code.</summary>
    private static Type Author(Type type)
    {
        while (type.DeclaringType is { } outer && type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
        {
            type = outer;
        }

        return type;
    }

    /// <summary>
    /// Whether <paramref name="method"/>'s body holds a <c>newobj</c> of a constructor of
    /// <paramref name="created"/>. The body is walked one instruction at a time, so an operand byte is
    /// never read as an opcode.
    /// </summary>
    private static bool Creates(MethodBase method, Type created)
    {
        if (method.GetMethodBody()?.GetILAsByteArray() is not { } il)
        {
            return false;
        }

        var typeArguments = method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var at = 0;

        while (at < il.Length)
        {
            var opCode = il[at] == TwoByteOpCodePrefix
                ? OpCodesByValue[unchecked((short)((TwoByteOpCodePrefix << 8) | il[at + 1]))]
                : OpCodesByValue[il[at]];
            at += opCode.Size;

            if (opCode == OpCodes.Newobj
                && method.Module.ResolveMethod(BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(at)), typeArguments, methodArguments)?.DeclaringType == created)
            {
                return true;
            }

            at += OperandSize(opCode, il, at);
        }

        return false;
    }

    private static int OperandSize(OpCode opCode, byte[] il, int at) => opCode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => sizeof(int) * (1 + BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(at))),
        _ => 4,
    };
}
