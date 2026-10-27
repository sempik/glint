using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Glint.Services.Capture;

/// <summary>
/// The handful of Direct3D 11 calls screen recording needs: a device, textures to copy captured frames into,
/// and the bridges between raw COM pointers and the WinRT capture and media types. Only a few methods are
/// used, so they are called through their vtable slots instead of declaring the huge interfaces.
/// The device exists only while a recording runs.
/// </summary>
internal static unsafe class D3D11Interop
{
    public const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    const int D3D_DRIVER_TYPE_HARDWARE = 1, D3D_DRIVER_TYPE_WARP = 5;
    const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    const uint D3D11_SDK_VERSION = 7;
    const uint D3D11_BIND_SHADER_RESOURCE = 0x8, D3D11_BIND_RENDER_TARGET = 0x20;

    // vtable slots (IUnknown takes 0-2)
    const int DeviceCreateTexture2D = 5;
    const int ContextCopySubresourceRegion = 46, ContextCopyResource = 47;
    const int TextureGetDesc = 10;
    const int MultithreadSetProtected = 5;
    const int DxgiAccessGetInterface = 3;
    const int CaptureInteropCreateForMonitor = 4;

    static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    static readonly Guid IID_IDXGISurface = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
    static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    static readonly Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
    static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
    static readonly Guid IID_IGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_BOX { public uint Left, Top, Front, Right, Bottom, Back; }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);

    [DllImport("combase.dll", ExactSpelling = true)]
    static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    static extern int WindowsDeleteString(IntPtr hstring);

    static IntPtr* Vtbl(IntPtr obj) => *(IntPtr**)obj;

    /// <summary>The COM pointer inside a projected WinRT object; borrowed, so not released by the caller.</summary>
    static IntPtr AbiOf(object winrt) => ((IWinRTObject)winrt).NativeObject.ThisPtr;

    /// <summary>A Direct3D 11 device (GPU, or the WARP software rasterizer as a fallback) and its immediate context.</summary>
    public sealed class Device : IDisposable
    {
        public IntPtr Native { get; private set; }
        public IntPtr Context { get; private set; }
        /// <summary>The same device as WinRT sees it, for the capture frame pool.</summary>
        public IDirect3DDevice Direct3D { get; }

        public Device()
        {
            var hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0,
                D3D11_SDK_VERSION, out var device, out _, out var context);
            if (hr < 0)
                hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0,
                    D3D11_SDK_VERSION, out device, out _, out context);
            Marshal.ThrowExceptionForHR(hr);
            Native = device;
            Context = context;
            try
            {
                // Capture callbacks, the media pipeline and our copies all use this device from different threads.
                if (Marshal.QueryInterface(device, in IID_ID3D10Multithread, out var mt) >= 0)
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Vtbl(mt)[MultithreadSetProtected])(mt, 1);
                    Marshal.Release(mt);
                }
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in IID_IDXGIDevice, out var dxgi));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                    try
                    {
                        Direct3D = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                    }
                    finally
                    {
                        Marshal.Release(inspectable);
                    }
                }
                finally
                {
                    Marshal.Release(dxgi);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>A GPU-only BGRA texture the encoder can read.</summary>
        public IntPtr CreateTexture(int width, int height)
        {
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleCount = 1,
                BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET,
            };
            IntPtr texture;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)Vtbl(Native)[DeviceCreateTexture2D])(Native, &desc, IntPtr.Zero, &texture);
            Marshal.ThrowExceptionForHR(hr);
            return texture;
        }

        /// <summary>Copies a rectangle of one texture into the top-left corner of another. Caller serialises.</summary>
        public void CopyRegion(IntPtr destination, IntPtr source, D3D11_BOX box)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D11_BOX*, void>)Vtbl(Context)[ContextCopySubresourceRegion])(
                Context, destination, 0, 0, 0, 0, source, 0, &box);
        }

        public void Copy(IntPtr destination, IntPtr source)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Vtbl(Context)[ContextCopyResource])(Context, destination, source);
        }

        public void Dispose()
        {
            if (Context != IntPtr.Zero) Marshal.Release(Context);
            if (Native != IntPtr.Zero) Marshal.Release(Native);
            Context = IntPtr.Zero;
            Native = IntPtr.Zero;
            (Direct3D as IDisposable)?.Dispose();
        }
    }

    public static D3D11_TEXTURE2D_DESC GetDesc(IntPtr texture)
    {
        D3D11_TEXTURE2D_DESC desc;
        ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void>)Vtbl(texture)[TextureGetDesc])(texture, &desc);
        return desc;
    }

    /// <summary>The ID3D11Texture2D behind a WinRT surface (a captured frame). The caller releases it.</summary>
    public static IntPtr GetTexture(IDirect3DSurface surface)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(AbiOf(surface), in IID_IDirect3DDxgiInterfaceAccess, out var access));
        try
        {
            IntPtr texture;
            var iid = IID_ID3D11Texture2D;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(access)[DxgiAccessGetInterface])(access, &iid, &texture);
            Marshal.ThrowExceptionForHR(hr);
            return texture;
        }
        finally
        {
            Marshal.Release(access);
        }
    }

    /// <summary>A WinRT surface wrapping one of our textures, which media samples are made from.</summary>
    public static IDirect3DSurface CreateSurface(IntPtr texture)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(texture, in IID_IDXGISurface, out var dxgi));
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgi, out var inspectable));
            try
            {
                return MarshalInterface<IDirect3DSurface>.FromAbi(inspectable);
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }
        finally
        {
            Marshal.Release(dxgi);
        }
    }

    /// <summary>A capture item for a whole monitor, without the system picker (desktop apps may do this).</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var hstring));
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, in IID_IGraphicsCaptureItemInterop, out var interop));
            try
            {
                IntPtr item;
                var iid = IID_IGraphicsCaptureItem;
                var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Vtbl(interop)[CaptureInteropCreateForMonitor])(interop, monitor, &iid, &item);
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    return GraphicsCaptureItem.FromAbi(item);
                }
                finally
                {
                    Marshal.Release(item);
                }
            }
            finally
            {
                Marshal.Release(interop);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    /// <summary>
    /// Windows 11 draws a yellow frame around a captured screen unless the app opts out; the switch only
    /// exists in newer SDKs than this project targets, so it is set through its interface directly.
    /// Older systems simply don't have the interface.
    /// </summary>
    public static void TryHideCaptureBorder(GraphicsCaptureSession session)
    {
        var iid = new Guid("f2cdd966-22ae-5ea1-9596-3a289344c3be"); // IGraphicsCaptureSession3
        if (Marshal.QueryInterface(AbiOf(session), in iid, out var session3) < 0) return;
        try
        {
            // IInspectable takes slots 3-5; get_IsBorderRequired is 6, put_IsBorderRequired 7.
            ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Vtbl(session3)[7])(session3, 0);
        }
        finally
        {
            Marshal.Release(session3);
        }
    }
}
