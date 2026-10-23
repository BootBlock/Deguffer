using System.Globalization;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.App.Controls;

/// <summary>
/// The macros <c>Cushions.hlsl</c> is compiled with: Core's numbers for the picture, and the layout
/// of the constants the renderer fills, so the shader states none of them itself. A name the shader
/// reads and this does not define fails the compilation, which <see cref="CushionRenderer"/> raises
/// with what the compiler said.
///
/// <para>Each macro is named for the member it is read from, so the shader says where its numbers
/// come from: <c>CushionShading_Ambient</c> is <see cref="CushionShading.Ambient"/>.</para>
///
/// <para>The pointer's light (<see cref="PointerLight"/>) has no number of its own in the shader: its
/// reach, strength and height reach it in <see cref="CushionConstants"/>, from Core, at every draw.</para>
/// </summary>
internal static class CushionMacros
{
    /// <summary>The macro the shader's <c>cbuffer</c> block is, whose members are <see cref="CushionConstants"/>'s fields.</summary>
    public const string Constants = nameof(CushionConstants) + "_Members";

    /// <summary>Every macro, by name and definition.</summary>
    public static IReadOnlyList<(string Name, string Definition)> All { get; } =
    [
        (Constants, ConstantBuffer.Members<CushionConstants>()),
        Number(nameof(CushionShading), nameof(CushionShading.Ambient), CushionShading.Ambient),
        Number(nameof(CushionShading), nameof(CushionShading.Diffuse), CushionShading.Diffuse),
        Number(nameof(CushionShading), nameof(CushionShading.LightX), CushionShading.LightX),
        Number(nameof(CushionShading), nameof(CushionShading.LightY), CushionShading.LightY),
        Number(nameof(CushionShading), nameof(CushionShading.LightZ), CushionShading.LightZ),
        Number(nameof(ShapeFinish), nameof(ShapeFinish.Whole), ShapeFinish.Whole),
        .. Enum.GetValues<MapShading>().Select(shading => Number(nameof(MapShading), shading.ToString(), (int)shading)),
    ];

    /// <summary>
    /// A number, written so HLSL reads it back as the same value: in full, and with a point whatever the
    /// culture.
    /// </summary>
    private static (string, string) Number(string type, string member, double value) =>
        ($"{type}_{member}", value.ToString("R", CultureInfo.InvariantCulture));
}
