using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CrimsonBrowser.Services;

/// <summary>
/// Real fullscreen: covers the taskbar (MarkFullscreenWindow) and sizes the window to the
/// monitor it currently sits on, so F11 works the same on multi-monitor setups.
/// </summary>
internal static class FullscreenHelper
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpShowWindow = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [ComImport]
    [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    private class TaskbarList { }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList2
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    }

    public static bool Enter(Window window)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;

            IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;

            Rect bounds = info.Monitor;
            SetWindowPos(handle, IntPtr.Zero,
                bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                SwpShowWindow);

            SetTaskbarFullscreen(handle, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Exit(Window window)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) SetTaskbarFullscreen(handle, false);
        }
        catch { }
    }

    private static void SetTaskbarFullscreen(IntPtr handle, bool fullscreen)
    {
        try
        {
            var taskbar = (ITaskbarList2)new TaskbarList();
            taskbar.HrInit();
            taskbar.MarkFullscreenWindow(handle, fullscreen);
        }
        catch
        {
            // Older shells may not expose ITaskbarList2; the resized window still works.
        }
    }
}
