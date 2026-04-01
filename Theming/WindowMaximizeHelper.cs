using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KaneCode.Theming;

/// <summary>
/// Fixes the maximize behavior for windows that use <c>WindowStyle="None"</c> with
/// <see cref="System.Windows.Shell.WindowChrome"/>. Without this fix the maximized window
/// covers the taskbar because Windows does not correctly constrain the bounds.
/// </summary>
internal sealed class WindowMaximizeHelper
{
    private const int WM_GETMINMAXINFO = 0x0024;

    private readonly Window _window;

    private WindowMaximizeHelper(Window window)
    {
        _window = window;
    }

    /// <summary>
    /// Attaches the maximize fix to <paramref name="window"/>. Call this once,
    /// typically from the window constructor or <c>SourceInitialized</c> handler.
    /// The hook is automatically removed when the window closes.
    /// </summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        WindowMaximizeHelper helper = new(window);

        // If the HwndSource is already available, hook immediately.
        // Otherwise, wait for SourceInitialized.
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            source.AddHook(helper.WindowProc);
        }
        else
        {
            window.SourceInitialized += helper.OnSourceInitialized;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _window.SourceInitialized -= OnSourceInitialized;

        if (HwndSource.FromVisual(_window) is HwndSource source)
        {
            source.AddHook(WindowProc);
        }
    }

    private nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }

        return nint.Zero;
    }

    /// <summary>
    /// Queries the monitor that contains <paramref name="hwnd"/> and constrains the
    /// maximized window position and size to that monitor's work area.
    /// </summary>
    private static void WmGetMinMaxInfo(nint hwnd, nint lParam)
    {
        MINMAXINFO mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        nint monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != nint.Zero)
        {
            MONITORINFO monitorInfo = new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                RECT workArea = monitorInfo.rcWork;
                RECT monitorArea = monitorInfo.rcMonitor;

                // Position is relative to the monitor's top-left corner
                mmi.ptMaxPosition.X = workArea.Left - monitorArea.Left;
                mmi.ptMaxPosition.Y = workArea.Top - monitorArea.Top;
                mmi.ptMaxSize.X = workArea.Right - workArea.Left;
                mmi.ptMaxSize.Y = workArea.Bottom - workArea.Top;
            }
        }

        Marshal.StructureToPtr(mmi, lParam, fDeleteOld: true);
    }

    #region Win32 interop

    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    #endregion
}
