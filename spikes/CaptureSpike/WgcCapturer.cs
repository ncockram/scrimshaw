using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace CaptureSpike;

/// <summary>Raw BGRA pixels; stride == width * 4.</summary>
internal sealed record RawImage(int Width, int Height, byte[] Bgra);

/// <summary>Shared D3D11 device plus its WinRT wrapper (what WGC needs).</summary>
internal sealed class D3DContext : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Immediate { get; }
    public IDirect3DDevice WinRtDevice { get; }
    public object Lock { get; } = new();

    public D3DContext()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out ID3D11Device? device, out ID3D11DeviceContext? ctx).CheckError();
        Device = device!;
        Immediate = ctx!;
        using IDXGIDevice dxgi = Device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr inspectable));
        WinRtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);
    }

    public void Dispose()
    {
        WinRtDevice.Dispose();
        Immediate.Dispose();
        Device.Dispose();
    }
}

/// <summary>
/// Keeps the most recent frame of one GraphicsCaptureItem in a GPU texture (copied on FrameArrived so the
/// pool never stalls), and reads it back to CPU memory on demand.
/// </summary>
internal sealed unsafe class WgcCapturer : IDisposable
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    private readonly D3DContext _d3d;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private ID3D11Texture2D? _latest;
    private ID3D11Texture2D? _staging;
    private Windows.Graphics.SizeInt32 _poolSize;

    public GraphicsCaptureItem Item { get; }
    public int FramesArrived { get; private set; }
    public bool Closed { get; private set; }

    public WgcCapturer(D3DContext d3d, GraphicsCaptureItem item, bool includeCursor)
    {
        _d3d = d3d;
        Item = item;
        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(d3d.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        item.Closed += (_, _) => Closed = true;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = includeCursor;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            try { _session.IsBorderRequired = false; }
            catch (Exception ex) { Console.WriteLine($"  (border could not be disabled: {ex.Message})"); }
        }
        _session.StartCapture();
    }

    public static GraphicsCaptureItem ForMonitor(IntPtr hmon) => Create(hmon, slot: 4);
    public static GraphicsCaptureItem ForWindow(IntPtr hwnd) => Create(hwnd, slot: 3);

    private static GraphicsCaptureItem Create(IntPtr handle, int slot)
    {
        IntPtr interop = Native.GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", IID_IGraphicsCaptureItemInterop);
        try
        {
            // IGraphicsCaptureItemInterop: [3] CreateForWindow(HWND, REFIID, void**), [4] CreateForMonitor(HMONITOR, REFIID, void**)
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)interop)[slot];
            Guid iid = IID_IGraphicsCaptureItem;
            IntPtr result;
            Marshal.ThrowExceptionForHR(fn(interop, handle, &iid, &result));
            try { return GraphicsCaptureItem.FromAbi(result); }
            finally { Marshal.Release(result); }
        }
        finally { Marshal.Release(interop); }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using Direct3D11CaptureFrame frame = sender.TryGetNextFrame();
        if (frame is null) return;
        FramesArrived++;
        var size = frame.ContentSize;
        using ID3D11Texture2D src = TextureFromSurface(frame.Surface);
        lock (_d3d.Lock)
        {
            if (_latest is null || _latest.Description.Width != size.Width || _latest.Description.Height != size.Height)
            {
                _latest?.Dispose();
                _staging?.Dispose();
                _latest = _d3d.Device.CreateTexture2D(Desc(size.Width, size.Height, ResourceUsage.Default, CpuAccessFlags.None));
                _staging = _d3d.Device.CreateTexture2D(Desc(size.Width, size.Height, ResourceUsage.Staging, CpuAccessFlags.Read));
            }
            int w = Math.Min(size.Width, (int)src.Description.Width), h = Math.Min(size.Height, (int)src.Description.Height);
            _d3d.Immediate.CopySubresourceRegion(_latest, 0, 0, 0, 0, src, 0, new Vortice.Mathematics.Box(0, 0, 0, w, h, 1));
        }
        if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
        {
            _poolSize = size;
            sender.Recreate(_d3d.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        }
    }

    /// <summary>Copies the most recent frame to CPU memory, or null if none has arrived yet.</summary>
    public RawImage? Grab()
    {
        lock (_d3d.Lock)
        {
            if (_latest is null || _staging is null) return null;
            _d3d.Immediate.CopyResource(_staging, _latest);
            int w = (int)_staging.Description.Width, h = (int)_staging.Description.Height;
            MappedSubresource map = _d3d.Immediate.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pixels = new byte[w * h * 4];
                fixed (byte* dst = pixels)
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, dst + (long)y * w * 4, w * 4, w * 4);
                return new RawImage(w, h, pixels);
            }
            finally { _d3d.Immediate.Unmap(_staging, 0); }
        }
    }

    private static Texture2DDescription Desc(int w, int h, ResourceUsage usage, CpuAccessFlags cpu) => new()
    {
        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
        Usage = usage, BindFlags = BindFlags.None, CPUAccessFlags = cpu, MiscFlags = ResourceOptionFlags.None,
    };

    private static ID3D11Texture2D TextureFromSurface(IDirect3DSurface surface)
    {
        IntPtr unk = ((IWinRTObject)surface).NativeObject.ThisPtr;
        Guid accessIid = IID_IDirect3DDxgiInterfaceAccess;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, in accessIid, out IntPtr access));
        try
        {
            // IDirect3DDxgiInterfaceAccess::GetInterface(REFIID, void**) is vtable slot 3
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)access)[3];
            Guid texIid = typeof(ID3D11Texture2D).GUID;
            IntPtr tex;
            Marshal.ThrowExceptionForHR(fn(access, &texIid, &tex));
            return new ID3D11Texture2D(tex);
        }
        finally { Marshal.Release(access); }
    }

    public void Dispose()
    {
        _session.Dispose();
        _pool.Dispose();
        lock (_d3d.Lock)
        {
            _latest?.Dispose();
            _staging?.Dispose();
        }
    }
}
