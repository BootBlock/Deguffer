using System.Numerics;
using System.Runtime.InteropServices;

namespace Deguffer.App.Controls;

/// <summary>
/// The few Direct3D 11 calls the map's cushions are drawn with (see <see cref="CushionRenderer"/>),
/// on the device Win2D already made.
///
/// <para><b>Slot order is the header's.</b> A <c>ComImport</c> interface is a vtable, so every slot
/// before the last one called is declared, in d3d11.h's order, and a slot never called is a
/// placeholder that takes nothing. A slot out of place calls the wrong method with no error.</para>
///
/// <para>The objects made on the device are kept as raw interface pointers rather than as wrappers:
/// they are only ever handed back to the device, several at a time in arrays, and released once. An
/// array is marked as a C array, because a <c>ComImport</c> interface passes one as a
/// <c>SAFEARRAY</c> otherwise, which the device refuses as an invalid argument.</para>
/// </summary>
internal static class Direct3D
{
    public const int FeatureLevel10 = 0xa000;

    public const uint BindVertexBuffer = 0x1;
    public const uint BindConstantBuffer = 0x4;
    public const uint BindShaderResource = 0x8;
    public const uint BindRenderTarget = 0x20;

    public const uint FormatR32G32B32A32Sint = 4;
    public const uint FormatR8G8B8A8Uint = 30;
    public const uint FormatR32Float = 41;
    public const uint FormatB8G8R8A8Unorm = 87;

    public const uint PerInstance = 1;

    /// <summary>DXGI_ADAPTER_FLAG_SOFTWARE: an adapter the CPU runs, WARP among them.</summary>
    public const uint SoftwareAdapter = 2;
    public const uint TriangleStrip = 5;

    /// <summary>Optimisation, and IEEE arithmetic, so the shader's sums stay as near the CPU's as a float can.</summary>
    public const uint CompileFlags = (1 << 15) | (1 << 13);

    public static readonly Guid DeviceId = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");

    public static readonly Guid DxgiSurfaceId = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    public static extern int D3DCompile(
        byte[] source,
        nuint length,
        string name,
        nint defines,
        nint include,
        string entry,
        string target,
        uint flags,
        uint effectFlags,
        out IBlob? code,
        out IBlob? errors);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int CreateDirect3D11SurfaceFromDXGISurface(nint surface, out nint graphicsSurface);

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferDescription
    {
        public uint ByteWidth;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
        public uint StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TextureDescription
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct InputElement
    {
        /// <summary>An ANSI string that outlives the layout made from it.</summary>
        public nint SemanticName;
        public uint SemanticIndex;
        public uint Format;
        public uint InputSlot;
        public uint AlignedByteOffset;
        public uint InputSlotClass;
        public uint InstanceDataStepRate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Viewport
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;
        public float MinDepth;
        public float MaxDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Box
    {
        public uint Left;
        public uint Top;
        public uint Front;
        public uint Right;
        public uint Bottom;
        public uint Back;
    }

    /// <summary>How a WinRT Direct3D device hands over the DXGI or Direct3D interface under it.</summary>
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface(in Guid id, out nint native);
    }

    /// <summary>The DXGI side of a Direct3D device, which names the adapter it runs on.</summary>
    [ComImport]
    [Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDxgiDevice
    {
        void SetPrivateData();

        void SetPrivateDataInterface();

        void GetPrivateData();

        void GetParent();

        [PreserveSig]
        int GetAdapter(out IDxgiAdapter adapter);
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDxgiAdapter
    {
        void SetPrivateData();

        void SetPrivateDataInterface();

        void GetPrivateData();

        void GetParent();

        void EnumOutputs();

        void GetDesc();

        void CheckInterfaceSupport();

        [PreserveSig]
        int GetDesc1(out AdapterDescription description);
    }

    [ComImport]
    [Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IBlob
    {
        [PreserveSig]
        nint GetBufferPointer();

        [PreserveSig]
        nuint GetBufferSize();
    }

    [ComImport]
    [Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDevice
    {
        [PreserveSig]
        int CreateBuffer(in BufferDescription description, nint initialData, out nint buffer);

        void CreateTexture1D();

        [PreserveSig]
        int CreateTexture2D(in TextureDescription description, nint initialData, out nint texture);

        void CreateTexture3D();

        void CreateShaderResourceView();

        void CreateUnorderedAccessView();

        [PreserveSig]
        int CreateRenderTargetView(nint resource, nint description, out nint view);

        void CreateDepthStencilView();

        [PreserveSig]
        int CreateInputLayout(
            [MarshalAs(UnmanagedType.LPArray)] InputElement[] elements,
            uint count,
            [MarshalAs(UnmanagedType.LPArray)] byte[] code,
            nuint length,
            out nint layout);

        [PreserveSig]
        int CreateVertexShader([MarshalAs(UnmanagedType.LPArray)] byte[] code, nuint length, nint linkage, out nint shader);

        void CreateGeometryShader();

        void CreateGeometryShaderWithStreamOutput();

        [PreserveSig]
        int CreatePixelShader([MarshalAs(UnmanagedType.LPArray)] byte[] code, nuint length, nint linkage, out nint shader);

        void CreateHullShader();

        void CreateDomainShader();

        void CreateComputeShader();

        void CreateClassLinkage();

        void CreateBlendState();

        void CreateDepthStencilState();

        void CreateRasterizerState();

        void CreateSamplerState();

        void CreateQuery();

        void CreatePredicate();

        void CreateCounter();

        void CreateDeferredContext();

        void OpenSharedResource();

        void CheckFormatSupport();

        void CheckMultisampleQualityLevels();

        void CheckCounterInfo();

        void CheckCounter();

        void CheckFeatureSupport();

        void GetPrivateData();

        void SetPrivateData();

        void SetPrivateDataInterface();

        [PreserveSig]
        int GetFeatureLevel();

        void GetCreationFlags();

        void GetDeviceRemovedReason();

        [PreserveSig]
        void GetImmediateContext(out IDeviceContext context);
    }

    [ComImport]
    [Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDeviceContext
    {
        void GetDevice();

        void GetPrivateData();

        void SetPrivateData();

        void SetPrivateDataInterface();

        [PreserveSig]
        void VSSetConstantBuffers(uint start, uint count, in nint buffers);

        void PSSetShaderResources();

        [PreserveSig]
        void PSSetShader(nint shader, nint classInstances, uint count);

        void PSSetSamplers();

        [PreserveSig]
        void VSSetShader(nint shader, nint classInstances, uint count);

        void DrawIndexed();

        void Draw();

        void Map();

        void Unmap();

        [PreserveSig]
        void PSSetConstantBuffers(uint start, uint count, in nint buffers);

        [PreserveSig]
        void IASetInputLayout(nint layout);

        [PreserveSig]
        void IASetVertexBuffers(uint start, uint count, in nint buffers, in uint strides, in uint offsets);

        void IASetIndexBuffer();

        void DrawIndexedInstanced();

        [PreserveSig]
        void DrawInstanced(uint verticesPerInstance, uint instances, uint startVertex, uint startInstance);

        void GSSetConstantBuffers();

        [PreserveSig]
        void GSSetShader(nint shader, nint classInstances, uint count);

        [PreserveSig]
        void IASetPrimitiveTopology(uint topology);

        void VSSetShaderResources();

        void VSSetSamplers();

        void Begin();

        void End();

        void GetData();

        void SetPredication();

        void GSSetShaderResources();

        void GSSetSamplers();

        [PreserveSig]
        void OMSetRenderTargets(uint count, in nint views, nint depthStencil);

        void OMSetRenderTargetsAndUnorderedAccessViews();

        [PreserveSig]
        void OMSetBlendState(nint state, nint factor, uint sampleMask);

        [PreserveSig]
        void OMSetDepthStencilState(nint state, uint reference);

        void SOSetTargets();

        void DrawAuto();

        void DrawIndexedInstancedIndirect();

        void DrawInstancedIndirect();

        void Dispatch();

        void DispatchIndirect();

        [PreserveSig]
        void RSSetState(nint state);

        [PreserveSig]
        void RSSetViewports(uint count, in Viewport viewport);

        void RSSetScissorRects();

        void CopySubresourceRegion();

        void CopyResource();

        /// <summary>A null <paramref name="box"/>, passed as a null reference, updates the whole resource.</summary>
        [PreserveSig]
        void UpdateSubresource(nint resource, uint subresource, in Box box, ref byte data, uint rowPitch, uint depthPitch);

        void CopyStructureCount();

        [PreserveSig]
        void ClearRenderTargetView(nint view, in Vector4 colour);
    }
}
