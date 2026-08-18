using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteControl;

/// <summary>
/// Streams the primary monitor to the phone as MJPEG over HTTP on port 5202 - a separate
/// socket from Server's 5201, so the video and input protocols never have to share framing.
///
/// Two lessons carried over from the sibling PhoneTrack project's earlier attempt at this
/// exact feature (see ../PhoneTrack/ForClaudeUseOnly.md §3b and the RemoteControl branch
/// log's 2026-08-18 entries), baked in from the start instead of discovered the hard way
/// again:
///
/// 1. The capture loop only does GDI work while <see cref="_viewerCount"/> is above zero -
///    PhoneTrack's first version ran CopyFromScreen continuously from the moment the
///    server started, whether or not a phone was even connected, and that alone was a
///    bigger input-lag source than the actual video bandwidth.
/// 2. Default quality is the cheapest preset, and the client can drop to OFF entirely -
///    video and input share the same Wi-Fi radio, so a heavy stream directly costs cursor
///    responsiveness no matter how well the capture loop itself behaves.
/// </summary>
class ScreenStreamer
{
    public const int Port = 5202;

    private const bool DrawCursor = true; // GDI capture omits the cursor - added back here
    private const string Boundary = "rcframe";

    public sealed class QualityPreset
    {
        public string Name = "";
        public int Width;
        public long Jpeg;
        public int Fps;
    }

    // Width matters most for bandwidth (scales roughly with its square); JPEG quality only
    // scales linearly. LOW is the default - see the class doc comment, point 2.
    public static readonly QualityPreset[] Presets =
    {
        new() { Name = "LOW", Width = 640, Jpeg = 40, Fps = 10 },
        new() { Name = "MED", Width = 960, Jpeg = 50, Fps = 12 },
        new() { Name = "HIGH", Width = 1280, Jpeg = 60, Fps = 15 },
        new() { Name = "MAX", Width = 1600, Jpeg = 75, Fps = 20 },
    };

    private volatile int _presetIndex;
    public QualityPreset CurrentPreset => Presets[_presetIndex];

    private void SetPreset(int index)
    {
        int clamped = Math.Clamp(index, 0, Presets.Length - 1);
        if (clamped == _presetIndex) return;
        _presetIndex = clamped;
        Log?.Invoke($"Quality -> {Presets[clamped].Name} ({Presets[clamped].Width}px, q{Presets[clamped].Jpeg}, {Presets[clamped].Fps}fps)");
    }

    // Shared newest-frame buffer - the capture loop writes here, every viewer's own loop
    // just reads the latest one, so opening the stream from a second device costs
    // bandwidth but not extra capture work.
    private readonly object _frameLock = new();
    private byte[]? _latestFrame;
    private int _frameNumber;

    // How many viewers are actively reading /stream.mjpg right now. Only counts real
    // streamers, not a plain "GET /" viewer-page request - see HandleViewerAsync.
    private int _viewerCount;

    private CancellationTokenSource _cts = new();

    // Screen visibility should follow the same "only the one paired, connected device" rule
    // the input protocol already enforces - without this check, ANY device on the LAN that
    // knows the PC's IP:5202 got a live stream immediately, whether or not it had ever been
    // added/trusted through the actual pairing flow on port 5201. Injected rather than a
    // direct reference to Server, so this class doesn't need to know anything about pairing/
    // trust itself - just "is someone actually allowed to be watching right now."
    private readonly Func<bool> _isDeviceConnected;

    public event Action<string>? Log;

    public ScreenStreamer(Func<bool> isDeviceConnected)
    {
        _isDeviceConnected = isDeviceConnected;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        Task.Run(() => CaptureLoopAsync(_cts.Token));
        Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop() => _cts.Cancel();

    // ── Capture loop ─────────────────────────────────────────────────────────────

    private async Task CaptureLoopAsync(CancellationToken token)
    {
        int screenWidth = GetSystemMetrics(SM_CXSCREEN);
        int screenHeight = GetSystemMetrics(SM_CYSCREEN);

        using var fullFrame = new Bitmap(screenWidth, screenHeight, PixelFormat.Format32bppArgb);
        using var fullGraphics = Graphics.FromImage(fullFrame);

        ImageCodecInfo jpegCodec = GetJpegCodec();
        var jpegBuffer = new MemoryStream();

        Bitmap? scaledFrame = null;
        Graphics? scaledGraphics = null;
        EncoderParameters? encoderSettings = null;
        int activePreset = -1;
        int scaledWidth = 0;
        int scaledHeight = 0;
        var frameInterval = TimeSpan.FromMilliseconds(100);

        while (!token.IsCancellationRequested)
        {
            // Nobody is watching - skip the GDI capture entirely instead of paying
            // ~15-30ms of CopyFromScreen every frame interval for no reason. See the
            // class doc comment.
            if (Volatile.Read(ref _viewerCount) == 0)
            {
                await Task.Delay(200, token).ContinueWith(_ => { });
                continue;
            }

            var frameStartedAt = DateTime.UtcNow;

            try
            {
                if (activePreset != _presetIndex)
                {
                    activePreset = _presetIndex;
                    QualityPreset preset = Presets[activePreset];

                    scaledWidth = Math.Min(preset.Width, screenWidth);
                    scaledHeight = screenHeight * scaledWidth / screenWidth;

                    scaledGraphics?.Dispose();
                    scaledFrame?.Dispose();

                    scaledFrame = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format32bppArgb);
                    scaledGraphics = Graphics.FromImage(scaledFrame);
                    scaledGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;

                    encoderSettings?.Dispose();
                    encoderSettings = new EncoderParameters(1);
                    encoderSettings.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, preset.Jpeg);

                    frameInterval = TimeSpan.FromMilliseconds(1000.0 / preset.Fps);

                    Log?.Invoke($"Capturing {screenWidth}x{screenHeight} -> {scaledWidth}x{scaledHeight} @ {preset.Fps}fps, q{preset.Jpeg}");
                }

                if (scaledFrame == null || scaledGraphics == null || encoderSettings == null) continue;

                fullGraphics.CopyFromScreen(0, 0, 0, 0, fullFrame.Size, CopyPixelOperation.SourceCopy);
                if (DrawCursor) OverlayCursor(fullGraphics);
                scaledGraphics.DrawImage(fullFrame, 0, 0, scaledWidth, scaledHeight);

                jpegBuffer.SetLength(0);
                scaledFrame.Save(jpegBuffer, jpegCodec, encoderSettings);

                lock (_frameLock)
                {
                    _latestFrame = jpegBuffer.ToArray();
                    _frameNumber++;
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Capture error: {ex.Message}");
            }

            var elapsed = DateTime.UtcNow - frameStartedAt;
            var remaining = frameInterval - elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token).ContinueWith(_ => { });
        }

        scaledGraphics?.Dispose();
        scaledFrame?.Dispose();
        encoderSettings?.Dispose();
    }

    private static ImageCodecInfo GetJpegCodec()
    {
        foreach (ImageCodecInfo encoder in ImageCodecInfo.GetImageEncoders())
        {
            if (encoder.FormatID == ImageFormat.Jpeg.Guid) return encoder;
        }
        throw new InvalidOperationException("No JPEG encoder available on this system.");
    }

    // ── Cursor overlay ───────────────────────────────────────────────────────────

    // Cached by cursor handle - Windows reuses the same handle per cursor shape, so this
    // settles to a handful of entries and avoids calling GetIconInfo (which allocates two
    // GDI bitmaps) every frame.
    private static readonly Dictionary<IntPtr, Point> _cursorHotspots = new();

    private static void OverlayCursor(Graphics target)
    {
        var cursorInfo = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref cursorInfo)) return;
        if (cursorInfo.flags != CURSOR_SHOWING) return;
        if (cursorInfo.hCursor == IntPtr.Zero) return;

        Point hotspot = GetCursorHotspot(cursorInfo.hCursor);
        int drawX = cursorInfo.ptScreenPos.x - hotspot.X;
        int drawY = cursorInfo.ptScreenPos.y - hotspot.Y;

        IntPtr hdc = target.GetHdc();
        try { DrawIconEx(hdc, drawX, drawY, cursorInfo.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL); }
        finally { target.ReleaseHdc(hdc); }
    }

    private static Point GetCursorHotspot(IntPtr cursorHandle)
    {
        if (_cursorHotspots.TryGetValue(cursorHandle, out Point cached)) return cached;

        var hotspot = new Point(0, 0);
        if (GetIconInfo(cursorHandle, out ICONINFO iconInfo))
        {
            hotspot = new Point(iconInfo.xHotspot, iconInfo.yHotspot);
            if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
            if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
        }
        _cursorHotspots[cursorHandle] = hotspot;
        return hotspot;
    }

    // ── HTTP server ──────────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        Log?.Invoke($"Screen stream ready on http://{Discovery.GetLocalAddress() ?? "<pc-ip>"}:{Port}/");

        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient viewer = await listener.AcceptTcpClientAsync(token);
                _ = HandleViewerAsync(viewer, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log?.Invoke($"Stream server error: {ex.Message}"); }
        finally { listener.Stop(); }
    }

    private async Task HandleViewerAsync(TcpClient viewer, CancellationToken token)
    {
        var endpoint = viewer.Client.RemoteEndPoint?.ToString() ?? "unknown";
        try
        {
            viewer.NoDelay = true;
            using NetworkStream stream = viewer.GetStream();
            string requestTarget = await ReadRequestPathAsync(stream);

            string requestPath = requestTarget;
            string requestQuery = "";
            int queryStart = requestTarget.IndexOf('?');
            if (queryStart >= 0)
            {
                requestPath = requestTarget[..queryStart];
                requestQuery = requestTarget[(queryStart + 1)..];
            }

            if (requestPath is "/" or "/index.html" or "/stream.mjpg")
            {
                // Nobody has actually paired/connected yet - refuse rather than start
                // streaming (or even showing the viewer page) to whoever asked. See the
                // _isDeviceConnected field doc comment.
                if (!_isDeviceConnected())
                {
                    await SendNotConnectedAsync(stream);
                    return;
                }

                if (requestPath is "/" or "/index.html")
                {
                    await SendViewerPageAsync(stream);
                }
                else
                {
                    int requested = ReadIntParam(requestQuery, "q", -1);
                    if (requested >= 0) SetPreset(requested);
                    await SendMjpegStreamAsync(stream, token);
                }
            }
            else
            {
                await SendNotFoundAsync(stream);
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log?.Invoke($"Viewer error ({endpoint}): {ex.Message}"); }
        finally { viewer.Dispose(); }
    }

    private static async Task<string> ReadRequestPathAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        int bytesRead = await stream.ReadAsync(buffer);
        if (bytesRead <= 0) return "";
        string request = Encoding.ASCII.GetString(buffer, 0, bytesRead);
        int lineEnd = request.IndexOf('\r');
        if (lineEnd < 0) lineEnd = request.Length;
        string[] parts = request[..lineEnd].Split(' ');
        return parts.Length >= 2 ? parts[1] : "";
    }

    private static int ReadIntParam(string query, string key, int fallback)
    {
        if (string.IsNullOrEmpty(query)) return fallback;
        foreach (string pair in query.Split('&'))
        {
            int equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            if (!pair[..equals].Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(pair[(equals + 1)..], out int value)) return value;
        }
        return fallback;
    }

    private static async Task SendViewerPageAsync(NetworkStream stream)
    {
        byte[] body = Encoding.UTF8.GetBytes(ViewerPageHtml);
        var headers = new StringBuilder();
        headers.Append("HTTP/1.1 200 OK\r\n");
        headers.Append("Content-Type: text/html; charset=utf-8\r\n");
        headers.Append($"Content-Length: {body.Length}\r\n");
        headers.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static async Task SendNotFoundAsync(NetworkStream stream)
    {
        const string response = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        await stream.FlushAsync();
    }

    // No device has paired/connected through the real input protocol yet - see the
    // _isDeviceConnected field doc comment. 403, not 404: the path is real, it's just not
    // available right now.
    private static async Task SendNotConnectedAsync(NetworkStream stream)
    {
        const string body = "Not connected - pair the device in RemoteControl first.";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = new StringBuilder();
        headers.Append("HTTP/1.1 403 Forbidden\r\n");
        headers.Append("Content-Type: text/plain; charset=utf-8\r\n");
        headers.Append($"Content-Length: {bodyBytes.Length}\r\n");
        headers.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()));
        await stream.WriteAsync(bodyBytes);
        await stream.FlushAsync();
    }

    private async Task SendMjpegStreamAsync(NetworkStream stream, CancellationToken token)
    {
        var headers = new StringBuilder();
        headers.Append("HTTP/1.1 200 OK\r\n");
        headers.Append($"Content-Type: multipart/x-mixed-replace; boundary={Boundary}\r\n");
        headers.Append("Cache-Control: no-store, no-cache, must-revalidate\r\n");
        headers.Append("Pragma: no-cache\r\n");
        headers.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), token);

        Interlocked.Increment(ref _viewerCount);
        try
        {
            int lastSentFrameNumber = -1;
            while (!token.IsCancellationRequested)
            {
                byte[]? frame = null;
                lock (_frameLock)
                {
                    if (_latestFrame != null && _frameNumber != lastSentFrameNumber)
                    {
                        frame = _latestFrame;
                        lastSentFrameNumber = _frameNumber;
                    }
                }

                if (frame == null)
                {
                    await Task.Delay(5, token);
                    continue;
                }

                var partHeader = new StringBuilder();
                partHeader.Append($"--{Boundary}\r\n");
                partHeader.Append("Content-Type: image/jpeg\r\n");
                partHeader.Append($"Content-Length: {frame.Length}\r\n\r\n");

                await stream.WriteAsync(Encoding.ASCII.GetBytes(partHeader.ToString()), token);
                await stream.WriteAsync(frame, token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), token);
                await stream.FlushAsync(token);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _viewerCount);
        }
    }

    private const string ViewerPageHtml = """
        <!doctype html>
        <html>
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=5">
          <title>RemoteControl — Screen</title>
          <style>
            html, body {
              margin: 0; padding: 0; height: 100%;
              background: #0f1115; overflow: hidden;
              display: flex; align-items: center; justify-content: center;
              -webkit-tap-highlight-color: transparent;
            }
            img { max-width: 100%; max-height: 100%; display: block; }
          </style>
        </head>
        <body>
          <img id="screen" src="/stream.mjpg" alt="PC screen">
          <script>
            document.body.addEventListener('click', function () {
              if (document.fullscreenElement) document.exitFullscreen();
              else if (document.documentElement.requestFullscreen) document.documentElement.requestFullscreen();
            });
            document.getElementById('screen').addEventListener('error', function () {
              var image = this;
              setTimeout(function () { image.src = '/stream.mjpg?t=' + Date.now(); }, 1000);
            });
          </script>
        </body>
        </html>
        """;

    // ── Win32 interop ────────────────────────────────────────────────────────────

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int CURSOR_SHOWING = 0x0001;
    private const int DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
