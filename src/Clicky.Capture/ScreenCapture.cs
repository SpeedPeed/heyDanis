using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace Clicky.Capture;

/// <summary>
/// Captures all connected displays as JPEG images, mirroring
/// <c>CompanionScreenCaptureUtility.captureAllScreensAsJPEG()</c> from the Mac reference.
/// Uses Windows.Graphics.Capture for capture with a GDI+ fallback.
/// </summary>
public static class ScreenCapture
{
    private const int MaxDimension = 3840;
    private const long JpegQuality = 75;

    /// <summary>
    /// Captures every connected display as a JPEG image.
    /// The cursor screen is always sorted first.
    /// Images are resized so the longer edge is <see cref="MaxDimension"/> pixels.
    /// </summary>
    /// <param name="excludeHwnds">
    /// Window handles to exclude from capture (overlay/tray windows).
    /// Used only when the WGC exclusion API is available.
    /// </param>
    public static Task<List<CapturedScreen>> CaptureAllScreensAsJpegAsync(
        IReadOnlyList<IntPtr>? excludeHwnds = null,
        bool drawGrid = false)
        => CaptureAllScreensAsJpegAsync(excludeHwnds, CancellationToken.None, drawGrid);

    public static async Task<List<CapturedScreen>> CaptureAllScreensAsJpegAsync(
        IReadOnlyList<IntPtr>? excludeHwnds, CancellationToken ct, bool drawGrid = false)
    {
        ct.ThrowIfCancellationRequested();
        // 1. Enumerate monitors via EnumDisplayMonitors
        var monitors = EnumerateMonitors();
        if (monitors.Count == 0)
            throw new InvalidOperationException("No display available for capture.");

        // 2. Get cursor position
        NativeMethods.GetCursorPos(out var cursorPos);
        var cursorPoint = new Point(cursorPos.X, cursorPos.Y);

        // 3. Sort with cursor screen first (stable sort preserves order otherwise)
        monitors.Sort((a, b) =>
        {
            bool aCursor = a.Bounds.Contains(cursorPoint);
            bool bCursor = b.Bounds.Contains(cursorPoint);
            if (aCursor && !bCursor) return -1;
            if (!aCursor && bCursor) return 1;
            return 0;
        });

        // 4. Try WGC capture; fall back to GDI+ if unavailable
        List<CapturedScreen>? results = null;

        if (GraphicsCaptureSession.IsSupported())
        {
            try
            {
                results = await CaptureWithWgcAsync(monitors, cursorPoint, drawGrid);
            }
            catch
            {
                // WGC setup can fail (no D3D device, older Windows, etc.)
                // Fall through to GDI+ fallback.
            }
        }

        results ??= CaptureWithGdi(monitors, cursorPoint, drawGrid);

        if (results.Count == 0)
            throw new InvalidOperationException("Failed to capture any screen.");

        return results;
    }

    /// <summary>
    /// Dumps all captured screens to %TEMP%\clicky-capture\ for smoke testing.
    /// </summary>
    public static async Task DumpToTempAsync()
    {
        var screens = await CaptureAllScreensAsJpegAsync();
        var dir = Path.Combine(Path.GetTempPath(), "clicky-capture");
        Directory.CreateDirectory(dir);

        for (int i = 0; i < screens.Count; i++)
        {
            var filename = $"screen_{i + 1}_{(screens[i].IsCursorScreen ? "cursor" : "secondary")}.jpg";
            await File.WriteAllBytesAsync(Path.Combine(dir, filename), screens[i].ImageBytes);
        }
    }

    // ── WGC capture path ───────────────────────────────────────────────

    private static async Task<List<CapturedScreen>> CaptureWithWgcAsync(
        List<MonitorInfo> monitors,
        Point cursorPoint,
        bool drawGrid)
    {
        // Create D3D11 device → IDXGIDevice → WinRT IDirect3DDevice
        using var d3dDevice = CreateDirect3DDevice();

        var results = new List<CapturedScreen>();

        for (int i = 0; i < monitors.Count; i++)
        {
            var monitor = monitors[i];
            var isCursor = monitor.Bounds.Contains(cursorPoint);

            // Create GraphicsCaptureItem from HMONITOR via COM interop
            var item = CreateCaptureItemForMonitor(monitor.Handle);
            var itemSize = item.Size;

            // Calculate scaled dimensions from monitor bounds (not itemSize) so that
            // ScreenshotPixelWidth/Height is derived from the same source as DisplayBounds.
            // The frame pool still uses itemSize (the actual WGC surface size).
            var (scaledW, scaledH) = CalculateScaledSize(monitor.Bounds.Width, monitor.Bounds.Height);

            if (itemSize.Width != monitor.Bounds.Width || itemSize.Height != monitor.Bounds.Height)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ScreenCapture] WGC itemSize ({itemSize.Width}x{itemSize.Height}) differs from " +
                    $"monitor bounds ({monitor.Bounds.Width}x{monitor.Bounds.Height}) for monitor {i}");
            }

            // Capture a single frame
            var jpegBytes = await CaptureSingleFrameAsync(d3dDevice, item, itemSize, scaledW, scaledH);

            if (jpegBytes is not null)
            {
                if (drawGrid)
                {
                    // Decode JPEG → GDI+ Bitmap, draw grid, re-encode
                    using var ms = new MemoryStream(jpegBytes);
                    using var bmp = new Bitmap(ms);
                    DrawCoordinateGrid(bmp);
                    jpegBytes = EncodeJpeg(bmp, JpegQuality);
                }

                results.Add(new CapturedScreen
                {
                    ImageBytes = jpegBytes,
                    Label = BuildLabel(monitors.Count, i, isCursor),
                    IsCursorScreen = isCursor,
                    DisplayBounds = monitor.Bounds,
                    ScreenshotPixelWidth = scaledW,
                    ScreenshotPixelHeight = scaledH,
                });
            }
        }

        return results;
    }

    private static async Task<byte[]?> CaptureSingleFrameAsync(
        IDirect3DDevice device,
        GraphicsCaptureItem item,
        SizeInt32 itemSize,
        int scaledW,
        int scaledH)
    {
        // Create the frame pool at native resolution; we resize after
        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            itemSize);

        using var session = framePool.CreateCaptureSession(item);

        // Hide the yellow capture border and cursor.
        // IsBorderRequired and IsCursorCaptureEnabled are Win11+ APIs
        // (SDK 10.0.20348+), so we call them via reflection to avoid
        // a compile error when targeting 10.0.19041.
        TrySetSessionProperty(session, "IsBorderRequired", false);
        TrySetSessionProperty(session, "IsCursorCaptureEnabled", false);

        var tcs = new TaskCompletionSource<Direct3D11CaptureFrame?>();

        framePool.FrameArrived += (pool, _) =>
        {
            var frame = pool.TryGetNextFrame();
            tcs.TrySetResult(frame);
        };

        session.StartCapture();

        // Wait up to 800 ms for the first frame (WGC is normally <300 ms on Win11;
        // a longer ceiling just delays the GDI+ fallback).
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        cts.Token.Register(() => tcs.TrySetResult(null));

        var frame = await tcs.Task;
        session.Dispose(); // Stop capture immediately

        if (frame is null) return null;

        using (frame)
        {
            // Convert the D3D surface to a SoftwareBitmap
            var softwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                frame.Surface,
                BitmapAlphaMode.Premultiplied);

            // Convert to BGRA8 if needed
            if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            }

            // Encode to JPEG with resize via WinRT BitmapEncoder
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
            encoder.SetSoftwareBitmap(softwareBitmap);
            encoder.BitmapTransform.ScaledWidth = (uint)scaledW;
            encoder.BitmapTransform.ScaledHeight = (uint)scaledH;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;

            // Set JPEG quality
            var properties = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue((float)JpegQuality / 100f, Windows.Foundation.PropertyType.Single) }
            };
            await encoder.FlushAsync();

            // Read the JPEG bytes from the stream
            stream.Seek(0);
            var reader = new Windows.Storage.Streams.DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            return bytes;
        }
    }

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        int hr = NativeMethods.D3D11CreateDevice(
            IntPtr.Zero,
            NativeMethods.D3D_DRIVER_TYPE_HARDWARE,
            IntPtr.Zero,
            NativeMethods.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero,
            0,
            NativeMethods.D3D11_SDK_VERSION,
            out var d3dDevicePtr,
            out _,
            out var contextPtr);

        Marshal.ThrowExceptionForHR(hr);

        try
        {
            // QueryInterface for IDXGIDevice
            var iidDxgi = NativeMethods.IID_IDXGIDevice;
            Marshal.QueryInterface(d3dDevicePtr, ref iidDxgi, out var dxgiDevicePtr);

            try
            {
                // Wrap as WinRT IDirect3DDevice
                hr = NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(
                    dxgiDevicePtr, out var inspectablePtr);
                Marshal.ThrowExceptionForHR(hr);

                try
                {
                    var device = (IDirect3DDevice)Marshal.GetObjectForIUnknown(inspectablePtr);
                    return device;
                }
                finally
                {
                    Marshal.Release(inspectablePtr);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevicePtr);
            }
        }
        finally
        {
            Marshal.Release(d3dDevicePtr);
            if (contextPtr != IntPtr.Zero) Marshal.Release(contextPtr);
        }
    }

    private static GraphicsCaptureItem CreateCaptureItemForMonitor(IntPtr hMonitor)
    {
        // Get the IGraphicsCaptureItemInterop activation factory
        var iid = NativeMethods.IID_IGraphicsCaptureItemInterop;
        NativeMethods.RoGetActivationFactory(
            "Windows.Graphics.Capture.GraphicsCaptureItem",
            ref iid,
            out var factoryPtr);

        try
        {
            var interop = (NativeMethods.IGraphicsCaptureItemInterop)
                Marshal.GetObjectForIUnknown(factoryPtr);

            var itemIid = NativeMethods.IID_IGraphicsCaptureItem;
            var itemPtr = interop.CreateForMonitor(hMonitor, ref itemIid);

            try
            {
                return (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        finally
        {
            Marshal.Release(factoryPtr);
        }
    }

    // ── GDI+ fallback capture path ─────────────────────────────────────

    private static List<CapturedScreen> CaptureWithGdi(
        List<MonitorInfo> monitors,
        Point cursorPoint,
        bool drawGrid)
    {
        var results = new List<CapturedScreen>();

        for (int i = 0; i < monitors.Count; i++)
        {
            var monitor = monitors[i];
            var isCursor = monitor.Bounds.Contains(cursorPoint);
            var bounds = monitor.Bounds;

            // Capture via GDI+ BitBlt
            using var fullBitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(fullBitmap))
            {
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            if (drawGrid)
                DrawCoordinateGrid(fullBitmap);

            // Resize to max 1280px on longer edge
            var (scaledW, scaledH) = CalculateScaledSize(bounds.Width, bounds.Height);
            using var resized = new Bitmap(scaledW, scaledH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(fullBitmap, 0, 0, scaledW, scaledH);
            }

            // Encode as JPEG at quality ~80
            var jpegBytes = EncodeJpeg(resized, JpegQuality);

            results.Add(new CapturedScreen
            {
                ImageBytes = jpegBytes,
                Label = BuildLabel(monitors.Count, i, isCursor),
                IsCursorScreen = isCursor,
                DisplayBounds = bounds,
                ScreenshotPixelWidth = scaledW,
                ScreenshotPixelHeight = scaledH,
            });
        }

        return results;
    }

    // ── Coordinate grid overlay ──────────────────────────────────────

    private const int MinorGridSpacing = 100;
    private const int GridSpacing = 200;

    /// <summary>
    /// Draws a subtle coordinate grid overlay on a GDI+ bitmap in-place.
    /// Grid lines every 200 pixels with coordinate labels at intersections.
    /// </summary>
    public static void DrawCoordinateGrid(Bitmap bitmap)
    {
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.Default;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var minorPen = new Pen(Color.FromArgb(38, 180, 180, 180), 1f);
        using var pen = new Pen(Color.FromArgb(86, 220, 220, 220), 1f);
        using var font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(Color.White);
        using var bgBrush = new SolidBrush(Color.FromArgb(160, 0, 0, 0));

        int w = bitmap.Width;
        int h = bitmap.Height;

        // Draw light 100 px minor lines first, then stronger 200 px labeled lines.
        for (int x = MinorGridSpacing; x < w; x += MinorGridSpacing)
            if (x % GridSpacing != 0)
                g.DrawLine(minorPen, x, 0, x, h);

        for (int y = MinorGridSpacing; y < h; y += MinorGridSpacing)
            if (y % GridSpacing != 0)
                g.DrawLine(minorPen, 0, y, w, y);

        // Draw vertical grid lines
        for (int x = GridSpacing; x < w; x += GridSpacing)
            g.DrawLine(pen, x, 0, x, h);

        // Draw horizontal grid lines
        for (int y = GridSpacing; y < h; y += GridSpacing)
            g.DrawLine(pen, 0, y, w, y);

        // Draw coordinate labels at intersections
        for (int x = GridSpacing; x < w; x += GridSpacing)
        {
            for (int y = GridSpacing; y < h; y += GridSpacing)
            {
                var label = $"{x},{y}";
                var size = g.MeasureString(label, font);

                // Offset label slightly from the intersection
                float lx = x + 3f;
                float ly = y + 3f;

                // Keep label within bitmap bounds
                if (lx + size.Width > w) lx = x - size.Width - 3f;
                if (ly + size.Height > h) ly = y - size.Height - 3f;

                // Background rectangle for contrast
                g.FillRectangle(bgBrush, lx - 1f, ly - 1f, size.Width + 2f, size.Height);
                g.DrawString(label, font, textBrush, lx, ly);
            }
        }
    }

    // ── Shared helpers ─────────────────────────────────────────────────

    private static void TrySetSessionProperty(GraphicsCaptureSession session, string propertyName, object value)
    {
        try
        {
            var prop = session.GetType().GetProperty(propertyName);
            prop?.SetValue(session, value);
        }
        catch
        {
            // Property not available on this Windows version.
        }
    }

    private static (int width, int height) CalculateScaledSize(int nativeW, int nativeH)
    {
        if (nativeW <= MaxDimension && nativeH <= MaxDimension)
            return (nativeW, nativeH);

        double aspect = (double)nativeW / nativeH;

        if (nativeW >= nativeH)
            return (MaxDimension, (int)Math.Round(MaxDimension / aspect));
        else
            return ((int)Math.Round(MaxDimension * aspect), MaxDimension);
    }

    /// <summary>
    /// Builds the screen label matching the Mac format:
    /// "user's screen (cursor is here)" for single display,
    /// "screen N of M — cursor is on this screen (primary focus)" / "… — secondary screen" for multi.
    /// </summary>
    public static string BuildLabel(int totalScreens, int displayIndex, bool isCursorScreen)
    {
        if (totalScreens == 1)
            return "user's screen (cursor is here)";

        if (isCursorScreen)
            return $"screen {displayIndex + 1} of {totalScreens} — cursor is on this screen (primary focus)";

        return $"screen {displayIndex + 1} of {totalScreens} — secondary screen";
    }

    private static byte[] EncodeJpeg(Bitmap bitmap, long quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        using var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);

        using var ms = new MemoryStream();
        bitmap.Save(ms, encoder, encoderParams);
        return ms.ToArray();
    }

    // ── Monitor enumeration ────────────────────────────────────────────

    public static List<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();
        s_monitorList = monitors;

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorEnumCallback, IntPtr.Zero);

        s_monitorList = null;
        return monitors;
    }

    [ThreadStatic]
    private static List<MonitorInfo>? s_monitorList;

    private static bool MonitorEnumCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT lprcMonitor, IntPtr dwData)
    {
        var info = new NativeMethods.MONITORINFOEX();
        info.cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>();

        if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
        {
            s_monitorList?.Add(new MonitorInfo
            {
                Handle = hMonitor,
                Bounds = new Rectangle(
                    info.rcMonitor.Left,
                    info.rcMonitor.Top,
                    info.rcMonitor.Width,
                    info.rcMonitor.Height),
                IsPrimary = (info.dwFlags & NativeMethods.MONITORINFOEX.MONITORINFOF_PRIMARY) != 0,
                DeviceName = info.szDevice,
            });
        }

        return true;
    }
}

/// <summary>Model for an enumerated display monitor.</summary>
public sealed class MonitorInfo
{
    public required IntPtr Handle { get; init; }
    public required Rectangle Bounds { get; init; }
    public required bool IsPrimary { get; init; }
    public required string DeviceName { get; init; }
}
