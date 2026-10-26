using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Glasstake.Capture;

/// <summary>
/// Creates GraphicsCaptureItems for HWNDs and HMONITORs (IGraphicsCaptureItemInterop).
/// </summary>
internal static class CaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    internal interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);

        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    public static GraphicsCaptureItem CreateForWindow(IntPtr hwnd)
    {
        var iid = GraphicsCaptureItemIid;
        return FromPointer(GetInterop().CreateForWindow(hwnd, ref iid));
    }

    public static GraphicsCaptureItem CreateForMonitor(IntPtr hmonitor)
    {
        var iid = GraphicsCaptureItemIid;
        return FromPointer(GetInterop().CreateForMonitor(hmonitor, ref iid));
    }

    private static IGraphicsCaptureItemInterop GetInterop()
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        return factory.AsInterface<IGraphicsCaptureItemInterop>();
    }

    private static GraphicsCaptureItem FromPointer(IntPtr itemPointer)
    {
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }
}

/// <summary>
/// Creates the WinRT IDirect3DDevice that Windows.Graphics.Capture needs, without any window or UI thread.
/// </summary>
internal static class Direct3D
{
    private const int DriverTypeHardware = 1;
    private const int DriverTypeWarp = 5;
    private const uint CreateDeviceBgraSupport = 0x20;
    private const uint SdkVersion = 7;

    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    public static IDirect3DDevice CreateDevice()
    {
        var hr = D3D11CreateDevice(IntPtr.Zero, DriverTypeHardware, IntPtr.Zero, CreateDeviceBgraSupport, IntPtr.Zero, 0, SdkVersion, out var d3dDevice, out _, out var context);

        if (hr < 0)
            hr = D3D11CreateDevice(IntPtr.Zero, DriverTypeWarp, IntPtr.Zero, CreateDeviceBgraSupport, IntPtr.Zero, 0, SdkVersion, out d3dDevice, out _, out context);

        Marshal.ThrowExceptionForHR(hr);

        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in DxgiDeviceIid, out var dxgiDevice));

            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable));

                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            Marshal.Release(context);
            Marshal.Release(d3dDevice);
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
