using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where each graphics driver writes its shader cache and what may be deleted there, as the GPU shader
/// caches row plans it and as §7.1 reads it from outside.
/// </summary>
internal static class GpuShaderCacheLayout
{
    /// <summary>
    /// What may be deleted under each vendor's directory. Anything not named here is Tier 4 by
    /// construction, which is the direction §5.2 requires the unknown case to fail in.
    ///
    /// AMD's names come from one published listing of <c>%LOCALAPPDATA%\AMD</c>, excerpted, from a
    /// Windows machine with AMD graphics whose users play games on it
    /// (https://gist.github.com/ccae7ef1d1446f4450005de139c601c4, October 2023). It shows five
    /// folders, and every file it shows in them is a hash-named driver blob: <c>DX9Cache</c> as
    /// <c>&lt;hash&gt;.bin</c>, and <c>DxCache</c>, <c>DxcCache</c>, <c>OglCache</c> and
    /// <c>VkCache</c> as <c>&lt;hash&gt;.parc</c>, the <c>OglCache</c> ones stamped by AMD's shader
    /// compiler. Its author reads the Direct3D names as Direct3D 9, 10 and 11, and 12. AMD's
    /// AMDVLK documentation names <c>VkCache</c> as the Vulkan pipeline cache on Linux, which
    /// agrees. <c>GLCache</c>, which third-party cleaners attribute to AMD too, appeared in no
    /// listing and is Tier 4: it is NVIDIA's name, and borrowing it is the guess §5.2 forbids.
    ///
    /// <c>CN</c> is named because it is the one thing beside the caches known to matter: AMD Software
    /// keeps its settings and per-game profiles there (<c>gmdb.blb</c>). The same listing says the
    /// root holds other files besides, unnamed, and a file in the root is never a candidate.
    ///
    /// A name that has not been seen in a directory is Tier 4 in that directory, so NVIDIA's
    /// LocalLow row is deliberately thin. Only <c>DXCache</c> was measured there, so
    /// <c>GLCache</c> is left out rather than assumed to exist in both tiers. Intel's LocalLow row is thin for the
    /// same reason: only <c>ShaderCache</c> was measured there.
    /// </summary>
    public static readonly IReadOnlyList<ShaderCacheRoot> Roots =
    [
        new ShaderCacheRoot(ProfileArea.LocalAppData, "NVIDIA", new DisposableChildSet(
        [
            new ChildClassification(
                "DXCache",
                SafetyTier.RegenerableCache,
                "Compiled Direct3D shader pipelines. The driver rebuilds each one the first time it is needed again."),
            new ChildClassification(
                "GLCache",
                SafetyTier.RegenerableCache,
                "Compiled OpenGL and Vulkan program binaries. The driver rebuilds them on demand."),
        ]),
        [("accounts", "NVIDIA account and sign-in state. It sits beside the caches and is not one.")]),
        new ShaderCacheRoot(ProfileArea.LocalLowAppData, "NVIDIA", new DisposableChildSet(
        [
            new ChildClassification(
                "DXCache",
                SafetyTier.RegenerableCache,
                "Compiled Direct3D shader pipelines. The driver rebuilds each one the first time it is needed again."),
        ]),
        [("accounts", "NVIDIA account and sign-in state. It is not a shader cache, and it is named "
            + "here whether or not this folder currently holds one.")]),
        new ShaderCacheRoot(ProfileArea.LocalAppData, "AMD", new DisposableChildSet(
        [
            new ChildClassification(
                "DX9Cache",
                SafetyTier.RegenerableCache,
                "Compiled Direct3D 9 shaders. The driver rebuilds each one the first time it is needed again."),
            new ChildClassification(
                "DxCache",
                SafetyTier.RegenerableCache,
                "Compiled Direct3D shader pipelines. The driver rebuilds each one the first time it is needed again."),
            new ChildClassification(
                "DxcCache",
                SafetyTier.RegenerableCache,
                "Compiled Direct3D 12 shader pipelines. The driver rebuilds each one the first time it is needed again."),
            new ChildClassification(
                "OglCache",
                SafetyTier.RegenerableCache,
                "Compiled OpenGL program binaries. The driver rebuilds them on demand."),
            new ChildClassification(
                "VkCache",
                SafetyTier.RegenerableCache,
                "Compiled Vulkan pipelines. The driver rebuilds them on demand."),
        ]),
        [("CN", "AMD Software's settings and per-game profiles. It sits beside the caches and is not one.")]),
        new ShaderCacheRoot(ProfileArea.LocalAppData, "Intel", new DisposableChildSet(
        [
            new ChildClassification(
                "ShaderCache",
                SafetyTier.RegenerableCache,
                "Compiled shader pipelines. The driver rebuilds each one the first time it is needed again."),
        ]), []),
        new ShaderCacheRoot(ProfileArea.LocalLowAppData, "Intel", new DisposableChildSet(
        [
            new ChildClassification(
                "ShaderCache",
                SafetyTier.RegenerableCache,
                "Compiled shader pipelines. The driver rebuilds each one the first time it is needed again."),
        ]), []),
    ];

    /// <summary>
    /// The vendor folder NVIDIA's driver writes inside a packaged application's container, and what
    /// may be deleted from it.
    ///
    /// <para>The two names the driver writes in the local application data, each measured inside a
    /// container. Nineteen containers held <c>DXCache</c>, and an OpenGL program linked from inside
    /// an AppContainer on an NVIDIA card wrote <c>AC\NVIDIA\GLCache</c>, laid out as it is outside
    /// one. Nothing else has been seen there, and anything else is Tier 4.</para>
    /// </summary>
    public static readonly PackagedShaderCacheRoot PackagedNvidia = new(
        "NVIDIA",
        new DisposableChildSet(
        [
            new ChildClassification(
                "DXCache",
                SafetyTier.RegenerableCache,
                "Compiled Direct3D shader pipelines the driver wrote for this app. It rebuilds each one "
                + "the first time it is needed again."),
            new ChildClassification(
                "GLCache",
                SafetyTier.RegenerableCache,
                "Compiled OpenGL and Vulkan program binaries the driver wrote for this app. It rebuilds "
                + "them on demand."),
        ]),
        [("accounts", "NVIDIA account and sign-in state. It is not a shader cache, and it is named "
            + "here whether or not this folder currently holds one.")]);

    /// <summary>The heading the packaged applications' caches are listed under, so they read as one.</summary>
    public const string PackagedGroup = "NVIDIA, inside packaged apps";
}
