using System.Runtime.InteropServices;
using Deguffer.Core.Exploring.Rendering;
using static Deguffer.App.Controls.Direct3D;

namespace Deguffer.App.Controls;

/// <summary>
/// The bytecode of the cushion shaders, compiled once for the process from the embedded
/// <c>Cushions.hlsl</c> with <see cref="CushionMacros"/>: the code is the same on every device.
///
/// <para>Compiled on a worker, which <see cref="Warm"/> starts as the first map's graphics are made
/// (see <see cref="MapGraphics"/>). Measured outside the app at 22 to 32 ms on a desktop processor,
/// and longer on a slower one, the compilation is several times the shading of a whole 4K canvas
/// (<c>docs/todo/composition-stack.md</c>). Started early, it overlaps whatever comes before the first
/// picture rather than holding up the UI thread as that picture is drawn. A renderer that asks before
/// it is done waits for the one compilation rather than starting another.</para>
/// </summary>
internal sealed class CushionShaders
{
    private readonly Lazy<Task<(byte[] Vertices, byte[] Pixels)>> _compiled;

    /// <param name="compile">What compiles the shaders. A test's own counts the compilations and holds one back.</param>
    internal CushionShaders(Func<(byte[] Vertices, byte[] Pixels)> compile) =>
        _compiled = new(() => Task.Run(compile));

    /// <summary>The shaders every renderer in the process is made with.</summary>
    public static CushionShaders Default { get; } = new(Compile);

    /// <summary>
    /// The compiled shaders, waited for where they are still being compiled. A failure is raised as the
    /// compiler raised it, not wrapped, so the message is the compiler's own.
    /// </summary>
    public (byte[] Vertices, byte[] Pixels) Value => _compiled.Value.GetAwaiter().GetResult();

    /// <summary>
    /// Start the compilation on a worker, where it has not started, and return it. A failure is left to
    /// <see cref="Value"/>, where the renderer that needs the shaders raises it.
    /// </summary>
    public Task<(byte[] Vertices, byte[] Pixels)> Warm() => _compiled.Value;

    /// <summary>
    /// Compile the shaders from the embedded source. A failure here is a defect in the shader or in the
    /// macros, raised with what the compiler said.
    /// </summary>
    internal static (byte[] Vertices, byte[] Pixels) Compile()
    {
        using var stream = typeof(CushionShaders).Assembly.GetManifestResourceStream("Deguffer.App.Controls.Cushions.hlsl")
            ?? throw new InvalidOperationException("The cushion shader is not embedded in the application.");
        using var copy = new MemoryStream();

        stream.CopyTo(copy);

        var source = copy.ToArray();
        var macros = new ShaderMacro[CushionMacros.All.Count + 1];

        try
        {
            for (var i = 0; i < CushionMacros.All.Count; i++)
            {
                var (name, definition) = CushionMacros.All[i];
                macros[i] = new ShaderMacro { Name = Marshal.StringToHGlobalAnsi(name), Definition = Marshal.StringToHGlobalAnsi(definition) };
            }

            return (Compile(source, macros, "vs", "vs_4_0"), Compile(source, macros, "ps", "ps_4_0"));
        }
        finally
        {
            foreach (var macro in macros)
            {
                Marshal.FreeHGlobal(macro.Name);
                Marshal.FreeHGlobal(macro.Definition);
            }
        }
    }

    private static byte[] Compile(byte[] source, ShaderMacro[] macros, string entry, string target)
    {
        var result = D3DCompile(source, (nuint)source.Length, "Cushions.hlsl", macros, 0, entry, target, CompileFlags, 0, out var code, out var errors);

        try
        {
            if (result < 0 || code is null)
            {
                throw new InvalidOperationException(
                    $"The cushion shader's {entry} did not compile (0x{result:X8}): {Text(errors)}");
            }

            var bytes = new byte[(int)code.GetBufferSize()];
            Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);

            return bytes;
        }
        finally
        {
            Release(code);
            Release(errors);
        }
    }

    private static string Text(IBlob? blob) =>
        blob is null ? "no message" : Marshal.PtrToStringAnsi(blob.GetBufferPointer(), (int)blob.GetBufferSize()).TrimEnd('\0', '\n', '\r');

    private static void Release(IBlob? blob)
    {
        if (blob is not null)
        {
            Marshal.ReleaseComObject(blob);
        }
    }
}
