using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BrimDeck.Native;

// While Start, search or another shell flyout is open, Windows moves the taskbar out of band 1, where every application
// window lives (topmost ones included), into a higher band of the shell's, and moves it back a second or two after the flyout
// closes. No ordinary window can follow it there, so a collapsed island over the taskbar is hidden behind it all that while.
// An ordinary process can still create a window in band 16, the band of explorer's task switcher, which lies above the
// lifted taskbar. While the taskbar under the island is lifted, the part of the island over the taskbar is drawn into such a
// window at its own place, a picture redrawn every frame; an open panel shows its part over the taskbar the same way, and
// nothing is drawn over the flyout above the taskbar. Both band calls are undocumented; where either is missing or refused,
// the mirror stays off and the island is covered as before.
//
// Presses, releases and wheel turns over the mirror are passed to the panel (Pointer), so the island and the open panel's part
// over the taskbar take clicks and the wheel as they do themselves; hovering the island opens it as always, as the panel reads
// the pointer's place. After a flyout closes, the taskbar can stay lifted for a few seconds while the pointer is over it, and
// the mirror stays with it.
internal sealed class TaskbarMirror : IDisposable
{
    private const uint DesktopBand = 1, SwitcherBand = 16;
    private const int FrameMilliseconds = 33;
    private const string ClassName = "BrimDeckTaskbarMirror";
    // The island in screen physical pixels, and the same island in the stage's own coordinates.
    private readonly Func<Rect?> _strip, _region;
    private readonly Func<bool> _wanted;
    private readonly Func<uint, Point, int, bool> _pointer;
    private readonly Visual _stage;
    // Puts the panel back above a taskbar that has come down to band 1, before the mirror leaves.
    private readonly Action _raise;
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(FrameMilliseconds) };
    // The taskbar is lifted a moment after the flyout takes the foreground, so a foreground change is followed by a few
    // quick checks; the order watch alone looks only every 400 ms.
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private int _followTicks;
    private IntPtr _window, _memory, _bitmap, _bits;
    private int _width, _height;
    private RenderTargetBitmap? _picture;
    private bool _shown, _unavailable;

    // pointer: a mouse message over the mirror, its place on screen in physical pixels and a wheel turn; true when handled.
    public TaskbarMirror(Visual stage, Func<Rect?> strip, Func<Rect?> region, Func<bool> wanted, Action raise, Func<uint, Point, int, bool> pointer)
    {
        _stage = stage; _strip = strip; _region = region; _wanted = wanted; _raise = raise; _pointer = pointer;
        _frame.Tick += (_, _) => Frame();
        _follow.Tick += (_, _) => { Check(); if (_shown || --_followTicks <= 0) _follow.Stop(); };
    }

    // Called after each check of the panel's order.
    public void Check()
    {
        if (_unavailable) return;
        if (_wanted() && Covered() is { } part) Show(part);
        else if (_shown) Hide();
    }

    // Called when another window takes the foreground.
    public void Follow()
    {
        if (_unavailable || _shown) return;
        _followTicks = 15;
        _follow.Start();
    }

    private void Frame()
    {
        if (_wanted() && Covered() is { } part) { Draw(part); return; }
        _raise();
        Hide();
    }

    // The part of the island, in screen physical pixels, over a taskbar that is out of band 1; null when there is none.
    private Rect? Covered()
    {
        if (_strip() is not { } strip) return null;
        try
        {
            foreach (var name in (ReadOnlySpan<string>)["Shell_TrayWnd", "Shell_SecondaryTrayWnd"])
                for (var bar = FindWindowEx(IntPtr.Zero, IntPtr.Zero, name, null); bar != IntPtr.Zero; bar = FindWindowEx(IntPtr.Zero, bar, name, null))
                {
                    if (!IsWindowVisible(bar) || !GetWindowRect(bar, out var r)) continue;
                    var part = Rect.Intersect(strip, new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
                    if (part.IsEmpty || !(part.Width >= 1) || !(part.Height >= 1)) continue;
                    if (GetWindowBand(bar, out var band) && band != DesktopBand && band != 0) return part;
                }
        }
        catch (EntryPointNotFoundException) { _unavailable = true; }
        return null;
    }

    private void Show(Rect part)
    {
        if (_shown) return;
        if (_window == IntPtr.Zero && !Create()) return;
        if (!Draw(part)) return;
        ShowWindow(_window, 4); // SW_SHOWNOACTIVATE
        _shown = true;
        _frame.Start();
    }

    private void Hide()
    {
        _frame.Stop();
        if (GetCapture() == _window) ReleaseCapture();
        if (!_shown) return;
        _shown = false;
        ShowWindow(_window, 0); // SW_HIDE
    }

    private bool Create()
    {
        try
        {
            // WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE; WS_POPUP. The mouse passes the clear pixels
            // of a layered window and stops at the drawn ones.
            if (Register())
                _window = CreateWindowInBand(0x08080000 | 0x80 | 0x08, ClassName, "BrimDeck", 0x80000000, 0, 0, 0, 0,
                    IntPtr.Zero, IntPtr.Zero, GetModuleHandle(IntPtr.Zero), IntPtr.Zero, SwitcherBand);
        }
        catch (EntryPointNotFoundException) { }
        if (_window == IntPtr.Zero) { _unavailable = true; return false; }
        Mirrors[_window] = this;
        _memory = CreateCompatibleDC(IntPtr.Zero);
        return true;
    }

    // One window class for the mirrors of every panel; the procedure finds its mirror by window.
    private static readonly Dictionary<IntPtr, TaskbarMirror> Mirrors = [];
    private static readonly WndProc Procedure = MirrorProcedure;
    private static bool _registered;
    private static bool Register()
    {
        if (_registered) return true;
        var type = new WNDCLASSEX
        {
            Size = Marshal.SizeOf<WNDCLASSEX>(), Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = GetModuleHandle(IntPtr.Zero), Cursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)), ClassName = ClassName, // IDC_ARROW
        };
        return _registered = RegisterClassEx(ref type) != 0;
    }
    private static IntPtr MirrorProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case 0x0021: return new IntPtr(3); // WM_MOUSEACTIVATE: MA_NOACTIVATE, the click is delivered without taking focus.
            // WM_SETCURSOR over the drawn island: the pointer keeps the shape the panel gives it.
            case 0x0020 when ((long)lParam & 0xFFFF) == 1: return new IntPtr(1);
            case >= 0x0201 and <= 0x020E when Mirrors.TryGetValue(window, out var mirror) && mirror.Pointer(message, wParam, lParam): return IntPtr.Zero;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    // Passes a press, a release or a wheel turn over the mirror to the panel, with its place on screen in physical pixels; the
    // panel acts on it as on the same place of itself (MainWindow.MirrorPointer). Mouse messages themselves cannot be handed
    // over: WPF takes no mouse input for its window while the pointer is over another window, and messages sent to the panel
    // were seen to raise no mouse events in it even while it held the capture. While a button is held over the mirror, the
    // mirror holds the capture, so the release comes back to it.
    private bool Pointer(uint message, IntPtr wParam, IntPtr lParam)
    {
        var point = new POINT { X = unchecked((short)(long)lParam), Y = unchecked((short)((long)lParam >> 16)) };
        // The wheel messages carry screen coordinates, the others the window's own.
        bool wheel = message is 0x020A or 0x020E;
        if (!wheel) ClientToScreen(_window, ref point);
        int delta = wheel ? unchecked((short)((long)wParam >> 16)) : 0;
        bool handled = _pointer(message, new Point(point.X, point.Y), delta);
        // After the panel has acted on it: a control that takes the capture on a press would otherwise keep the release from
        // the mirror.
        if (message is 0x0201 or 0x0204 or 0x0207) SetCapture(_window);       // WM_LBUTTONDOWN, WM_RBUTTONDOWN, WM_MBUTTONDOWN
        else if (message is 0x0202 or 0x0205 or 0x0208 && GetCapture() == _window) ReleaseCapture();
        return handled;
    }

    // Draws the given part of the island as it is now into the mirror, at that part's place on screen. The island's place on
    // screen and in the stage's parent are both upright (the edge's turn is inside the stage), so the part maps across by scale.
    private bool Draw(Rect part)
    {
        if (_strip() is not { } strip || _region() is not { } island || !(island.Width > 0) || !(island.Height > 0) || !(strip.Width > 0) || !(strip.Height > 0)) return false;
        double scaleX = island.Width / strip.Width, scaleY = island.Height / strip.Height;
        int left = (int)Math.Round(part.X), top = (int)Math.Round(part.Y);
        int width = (int)Math.Round(part.Right) - left, height = (int)Math.Round(part.Bottom) - top;
        if (width <= 0 || height <= 0) return false;
        var region = new Rect(island.X + (left - strip.X) * scaleX, island.Y + (top - strip.Y) * scaleY, width * scaleX, height * scaleY);
        double dpiX = 96.0 * width / region.Width, dpiY = 96.0 * height / region.Height;
        if (_picture is null || _picture.PixelWidth != width || _picture.PixelHeight != height
            || Math.Abs(_picture.DpiX - dpiX) > 0.01 || Math.Abs(_picture.DpiY - dpiY) > 0.01)
            _picture = new RenderTargetBitmap(width, height, dpiX, dpiY, PixelFormats.Pbgra32);
        if ((width != _width || height != _height) && !Allocate(width, height)) return false;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(_stage) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = region, Stretch = Stretch.Fill },
                null, new Rect(0, 0, region.Width, region.Height));
        _picture.Clear();
        _picture.Render(drawing);
        // Pbgra32 is the premultiplied BGRA a layered window takes.
        _picture.CopyPixels(new Int32Rect(0, 0, width, height), _bits, width * height * 4, width * 4);
        var place = new POINT { X = left, Y = top };
        var size = new SIZE { Width = width, Height = height };
        var origin = new POINT();
        var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
        return UpdateLayeredWindow(_window, IntPtr.Zero, ref place, ref size, _memory, ref origin, 0, ref blend, 2); // ULW_ALPHA
    }

    private bool Allocate(int width, int height)
    {
        var header = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var bitmap = CreateDIBSection(_memory, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero) return false;
        SelectObject(_memory, bitmap);
        if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
        _bitmap = bitmap; _bits = bits; _width = width; _height = height;
        return true;
    }

    public void Dispose()
    {
        _frame.Stop(); _follow.Stop();
        if (GetCapture() == _window) ReleaseCapture();
        if (_window != IntPtr.Zero) { Mirrors.Remove(_window); DestroyWindow(_window); }
        if (_memory != IntPtr.Zero) DeleteDC(_memory);
        if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
        _window = _memory = _bitmap = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size, Width, Height; public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int Size; public uint Style; public IntPtr Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public IntPtr SmallIcon;
    }
    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")] private static extern ushort RegisterClassEx(ref WNDCLASSEX type);
    [DllImport("user32", EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32", EntryPoint = "LoadCursorW")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32")] private static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32")] private static extern IntPtr GetCapture();
    [DllImport("user32")] private static extern bool ReleaseCapture();
    [DllImport("user32")] private static extern bool ClientToScreen(IntPtr window, ref POINT point);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowInBand(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter, uint band);
    [DllImport("user32")] private static extern bool GetWindowBand(IntPtr window, out uint band);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32")] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref POINT place, ref SIZE size, IntPtr source,
        ref POINT origin, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("kernel32", EntryPoint = "GetModuleHandleW")] private static extern IntPtr GetModuleHandle(IntPtr name);
    [DllImport("gdi32")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr item);
}
