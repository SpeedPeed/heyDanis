using System.Runtime.InteropServices;

namespace Clicky.Capture;

/// <summary>
/// P/Invoke declarations for monitor enumeration and cursor position.
/// </summary>
internal static class NativeMethods
{
    // ── Monitor enumeration ────────────────────────────────────────────

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;

        public const uint MONITORINFOF_PRIMARY = 1;
    }

    // ── Cursor position ────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // ── D3D11 device creation ──────────────────────────────────────────

    public const int D3D_DRIVER_TYPE_HARDWARE = 1;
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    public const uint D3D11_SDK_VERSION = 7;

    [DllImport("d3d11.dll", PreserveSig = true)]
    public static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int DriverType,
        IntPtr Software,
        uint Flags,
        IntPtr pFeatureLevels,
        uint FeatureLevels,
        uint SDKVersion,
        out IntPtr ppDevice,
        out IntPtr pFeatureLevel,
        out IntPtr ppImmediateContext);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = true)]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);

    // ── WinRT activation factory ───────────────────────────────────────

    [DllImport("combase.dll", PreserveSig = false)]
    public static extern void RoGetActivationFactory(
        [MarshalAs(UnmanagedType.HString)] string activatableClassId,
        [In] ref Guid iid,
        out IntPtr factory);

    // ── IGraphicsCaptureItemInterop COM interface ──────────────────────

    public static readonly Guid IID_IGraphicsCaptureItemInterop =
        new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    public static readonly Guid IID_IGraphicsCaptureItem =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static readonly Guid IID_IDXGIDevice =
        new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(
            IntPtr window,
            [In] ref Guid iid);

        IntPtr CreateForMonitor(
            IntPtr monitor,
            [In] ref Guid iid);
    }

    // ── IMemoryBufferByteAccess for SoftwareBitmap pixel reading ──────

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
