using System.Runtime.InteropServices;
using System.Windows.Interop;
using FusePlayer.Playback;

namespace FusePlayer.Controls;

/// <summary>
/// Native child window rendered directly by libmpv. WPF only positions the
/// surface; video frames stay on the Direct3D path and are never copied through
/// a WriteableBitmap.
/// </summary>
public sealed class VideoHwndHost : HwndHost, IDisposable
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int CsHRedraw = 0x0002;
    private const int CsVRedraw = 0x0001;
    private const int BlackBrush = 4;
    private const int ErrorClassAlreadyExists = 1410;
    private const int IdcArrow = 32512;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int WmSysCommand = 0x0112;
    private const int HitBottomLeft = 16;
    private const int HitBottomRight = 17;
    private const int ScSize = 0xF000;
    private const int SizeBottomLeft = 7;
    private const int SizeBottomRight = 8;
    private const int GwlStyle = -16;
    private const long WsThickFrame = 0x00040000L;
    private const uint GaRoot = 2;
    private const string WindowClassName = "FuzeMpvVideoSurface";

    private static readonly object RegistrationLock = new();
    private static readonly WindowProcedure WindowCallback = SurfaceWindowProcedure;
    private static ushort _windowClassAtom;

    private IntPtr _nativeHandle;
    private MpvPlayer? _attachedPlayer;
    private bool _disposed;
    private static bool _cursorHidden;

    public IntPtr NativeHandle => _nativeHandle;

    public static void SetVideoCursorHidden(bool hidden)
    {
        _cursorHidden = hidden;
        if (!hidden)
            SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(IdcArrow)));
    }

    public event EventHandler? HandleCreated;

    public void Attach(MpvPlayer player)
    {
        if (_disposed)
            return;

        _attachedPlayer = player;
        if (_nativeHandle != IntPtr.Zero)
            player.AttachWindow(_nativeHandle);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureWindowClass();
        var instance = GetModuleHandle(null);
        _nativeHandle = CreateWindowEx(
            0,
            WindowClassName,
            string.Empty,
            WsChild | WsVisible | WsClipSiblings | WsClipChildren,
            0,
            0,
            Math.Max(1, (int)ActualWidth),
            Math.Max(1, (int)ActualHeight),
            hwndParent.Handle,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (_nativeHandle == IntPtr.Zero)
            throw new InvalidOperationException($"Impossible de créer la surface vidéo (Win32 {Marshal.GetLastWin32Error()}).");

        _attachedPlayer?.AttachWindow(_nativeHandle);
        HandleCreated?.Invoke(this, EventArgs.Empty);
        return new HandleRef(this, _nativeHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
            DestroyWindow(hwnd.Handle);
        if (_nativeHandle == hwnd.Handle)
            _nativeHandle = IntPtr.Zero;
    }

    private static void EnsureWindowClass()
    {
        if (_windowClassAtom != 0)
            return;

        lock (RegistrationLock)
        {
            if (_windowClassAtom != 0)
                return;

            var instance = GetModuleHandle(null);
            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                Style = CsHRedraw | CsVRedraw,
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowCallback),
                Instance = instance,
                Cursor = LoadCursor(IntPtr.Zero, new IntPtr(IdcArrow)),
                Background = GetStockObject(BlackBrush),
                ClassName = WindowClassName
            };

            _windowClassAtom = RegisterClassEx(ref windowClass);
            if (_windowClassAtom == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
                throw new InvalidOperationException(
                    $"Impossible d'enregistrer la surface vidéo (Win32 {Marshal.GetLastWin32Error()}).");
            if (_windowClassAtom == 0)
                _windowClassAtom = 1;
        }
    }

    private static IntPtr SurfaceWindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        // Quand l'overlay WPF laisse passer un coin, c'est ce HWND enfant
        // libmpv qui reçoit encore le hit-test. Reproduire ici le comportement
        // natif de mpv évite que la surface vidéo transforme le coin en zone
        // cliente et bloque le redimensionnement du parent.
        if (message == WmNcHitTest && TryGetResizeHit(window, out var hit))
            return new IntPtr(hit);

        if (message == WmNcLeftButtonDown &&
            wParam.ToInt32() is HitBottomLeft or HitBottomRight)
        {
            var root = GetAncestor(window, GaRoot);
            if (root != IntPtr.Zero)
            {
                ReleaseCapture();
                var edge = wParam.ToInt32() == HitBottomLeft
                    ? SizeBottomLeft
                    : SizeBottomRight;
                SendMessage(root, WmSysCommand,
                    new IntPtr(ScSize | edge), IntPtr.Zero);
                return IntPtr.Zero;
            }
        }

        if (message == 0x0020 && _cursorHidden)
        {
            SetCursor(IntPtr.Zero);
            return new IntPtr(1);
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private static bool TryGetResizeHit(IntPtr window, out int hit)
    {
        hit = 0;
        var root = GetAncestor(window, GaRoot);
        if (root == IntPtr.Zero || IsZoomed(root))
            return false;

        var style = GetWindowLongPtr(root, GwlStyle).ToInt64();
        if ((style & WsThickFrame) == 0 || !GetWindowRect(root, out var bounds) ||
            !GetCursorPos(out var cursor))
            return false;

        var dpi = GetDpiForWindow(root);
        if (dpi == 0)
            dpi = 96;
        var corner = Math.Max(7, (int)Math.Ceiling(7 * dpi / 96d));
        var inBottom = cursor.Y >= bounds.Bottom - corner && cursor.Y < bounds.Bottom;
        var inLeft = cursor.X >= bounds.Left && cursor.X < bounds.Left + corner;
        var inRight = cursor.X >= bounds.Right - corner && cursor.X < bounds.Right;
        if (!inBottom || (!inLeft && !inRight))
            return false;

        hit = inLeft ? HitBottomLeft : HitBottomRight;
        return true;
    }

    public new void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _attachedPlayer = null;
        base.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal IntPtr WindowProcedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal IntPtr Instance;
        internal IntPtr Icon;
        internal IntPtr Cursor;
        internal IntPtr Background;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] internal string ClassName;
        internal IntPtr SmallIcon;
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int objectIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}
