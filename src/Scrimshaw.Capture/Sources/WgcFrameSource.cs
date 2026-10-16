using System.Runtime.InteropServices;
using Scrimshaw.Capture.Interop;
using Scrimshaw.Core.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Scrimshaw.Capture.Sources;

/// <summary>A D3D11 device plus the WinRT wrapper Windows.Graphics.Capture needs. Shared by a session's sources.</summary>
public sealed class D3DDevice : IDisposable
{
    public D3DDevice()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        Device = device!;
        Context = context!;
        using IDXGIDevice dxgi = Device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr inspectable));
        try
        {
            WinRtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    internal ID3D11Device Device { get; }
    internal ID3D11DeviceContext Context { get; }
    internal IDirect3DDevice WinRtDevice { get; }

    /// <summary>The immediate context isn't thread-safe; every use goes through this lock.</summary>
    internal Lock ContextLock { get; } = new();

    public void Dispose()
    {
        WinRtDevice.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}

/// <summary>
/// One monitor or window via Windows.Graphics.Capture. Each arriving frame is copied into a GPU texture
/// straight away, so the frame pool never stalls; <see cref="Grab"/> reads that texture back to memory.
/// WGC only delivers frames when content changes, so "latest" may be several seconds old: that's fine.
/// Verified in Phase 0 on this machine (findings F10–F13).
/// </summary>
public sealed unsafe class WgcFrameSource : IFrameSource
{
    private const int PoolBuffers = 2;
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    private readonly D3DDevice _d3d;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly IntPtr _window;
    private Windows.Graphics.SizeInt32 _poolSize;
    private ID3D11Texture2D? _latest;
    private ID3D11Texture2D? _staging;
    private volatile bool _closed;
    private bool _disposed;

    private WgcFrameSource(D3DDevice d3d, GraphicsCaptureItem item, bool includeCursor, IntPtr window = default)
    {
        _d3d = d3d;
        _window = window;
        _item = item;
        _item.Closed += OnItemClosed;
        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(d3d.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = includeCursor; // FR-C7
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            // Turning the yellow border off is allowed for unpackaged desktop apps on Windows 11; Windows 10 keeps it (A6).
            try
            {
                _session.IsBorderRequired = false;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (COMException)
            {
            }
        }
        _session.StartCapture();
    }

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    /// <summary>
    /// True once WGC reports the item closed, or (for windows) the window no longer exists. The window
    /// check matters: when a process is killed, the Closed event isn't reliably raised (Phase 2 desktop tests).
    /// </summary>
    public bool IsClosed => _closed || (_window != IntPtr.Zero && !NativeMethods.IsWindow(_window));

    /// <summary>Number of frames WGC has delivered (diagnostics).</summary>
    public int FramesArrived { get; private set; }

    public static WgcFrameSource ForMonitor(D3DDevice d3d, IntPtr monitor, bool includeCursor) =>
        new(d3d, CreateItem(monitor, slot: 4), includeCursor);

    public static WgcFrameSource ForWindow(D3DDevice d3d, IntPtr hwnd, bool includeCursor) =>
        new(d3d, CreateItem(hwnd, slot: 3), includeCursor, hwnd);

    public RawImage? Grab()
    {
        lock (_d3d.ContextLock)
        {
            if (_disposed || _latest is null || _staging is null) return null;
            _d3d.Context.CopyResource(_staging, _latest);
            int w = (int)_staging.Description.Width, h = (int)_staging.Description.Height, stride = w * 4;
            MappedSubresource map = _d3d.Context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pixels = new byte[stride * h];
                fixed (byte* dst = pixels)
                {
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, dst + (long)y * stride, stride, stride);
                }
                return new RawImage(w, h, stride, pixels);
            }
            finally
            {
                _d3d.Context.Unmap(_staging, 0);
            }
        }
    }

    public void Dispose()
    {
        lock (_d3d.ContextLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _item.Closed -= OnItemClosed;
        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
        lock (_d3d.ContextLock)
        {
            _latest?.Dispose();
            _staging?.Dispose();
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => _closed = true;

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
        if (frame is null) return;
        Windows.Graphics.SizeInt32 size = frame.ContentSize;
        using (ID3D11Texture2D source = TextureFromSurface(frame.Surface))
        {
            lock (_d3d.ContextLock)
            {
                if (_disposed) return;
                FramesArrived++;
                if (_latest is null || _latest.Description.Width != size.Width || _latest.Description.Height != size.Height)
                {
                    _latest?.Dispose();
                    _staging?.Dispose();
                    _latest = _d3d.Device.CreateTexture2D(Describe(size.Width, size.Height, ResourceUsage.Default, CpuAccessFlags.None));
                    _staging = _d3d.Device.CreateTexture2D(Describe(size.Width, size.Height, ResourceUsage.Staging, CpuAccessFlags.Read));
                }
                int w = Math.Min(size.Width, (int)source.Description.Width), h = Math.Min(size.Height, (int)source.Description.Height);
                _d3d.Context.CopySubresourceRegion(_latest, 0, 0, 0, 0, source, 0, new Vortice.Mathematics.Box(0, 0, 0, w, h, 1));
            }
        }

        // A resized window delivers frames of the new size only after the pool is recreated.
        if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
        {
            _poolSize = size;
            sender.Recreate(_d3d.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, size);
        }
    }

    private static Texture2DDescription Describe(int w, int h, ResourceUsage usage, CpuAccessFlags cpu) => new()
    {
        Width = (uint)w,
        Height = (uint)h,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = usage,
        BindFlags = BindFlags.None,
        CPUAccessFlags = cpu,
        MiscFlags = ResourceOptionFlags.None,
    };

    private static GraphicsCaptureItem CreateItem(IntPtr handle, int slot)
    {
        IntPtr interop = NativeMethods.GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", IidGraphicsCaptureItemInterop);
        try
        {
            // IGraphicsCaptureItemInterop vtable: [3] CreateForWindow(HWND, REFIID, void**), [4] CreateForMonitor(HMONITOR, REFIID, void**)
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)interop)[slot];
            Guid iid = IidGraphicsCaptureItem;
            IntPtr result;
            Marshal.ThrowExceptionForHR(create(interop, handle, &iid, &result));
            try
            {
                return GraphicsCaptureItem.FromAbi(result);
            }
            finally
            {
                Marshal.Release(result);
            }
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    private static ID3D11Texture2D TextureFromSurface(IDirect3DSurface surface)
    {
        IntPtr unknown = ((IWinRTObject)surface).NativeObject.ThisPtr;
        Guid accessIid = IidDirect3DDxgiInterfaceAccess;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in accessIid, out IntPtr access));
        try
        {
            // IDirect3DDxgiInterfaceAccess::GetInterface(REFIID, void**) is vtable slot 3.
            var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)access)[3];
            Guid textureIid = typeof(ID3D11Texture2D).GUID;
            IntPtr texture;
            Marshal.ThrowExceptionForHR(getInterface(access, &textureIid, &texture));
            return new ID3D11Texture2D(texture);
        }
        finally
        {
            Marshal.Release(access);
        }
    }
}
