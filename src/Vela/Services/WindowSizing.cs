using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Vela.Services;

internal static class WindowSizing
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public Rect NormalPosition;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);

    [ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    private class TaskbarList { }

    [ComImport, Guid("602D4995-B13A-429B-A66E-1935E44F4317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList2
    {
        void HrInit();
        void AddTab(IntPtr window);
        void DeleteTab(IntPtr window);
        void ActivateTab(IntPtr window);
        void SetActiveAlt(IntPtr window);
        void MarkFullscreenWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    }

    private static readonly HashSet<Window> FullscreenWindows = new();
    private static ITaskbarList2? _taskbar;

    internal readonly record struct Placement(int Left, int Top, int Width, int Height);

    internal static void Install(Window window)
    {
        var originalMinWidth = window.MinWidth; var originalMinHeight = window.MinHeight;
        void Fit()
        {
            if (IsFullscreen(window)) return;
            var handle = new WindowInteropHelper(window).Handle;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (handle == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(handle, MonitorDefaultToNearest), ref info)) return;
            var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice;
            var scale = transform?.M11 ?? 1;
            window.MinWidth = originalMinWidth; window.MinHeight = originalMinHeight;
            FitAvailable(window, (info.Work.Right - info.Work.Left) / scale, (info.Work.Bottom - info.Work.Top) / scale);
        }
        window.Loaded += (_, _) => Fit();
        window.SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            { if (message is 0x02E0 or 0x007E) window.Dispatcher.BeginInvoke(Fit); return IntPtr.Zero; });
        };
    }

    internal static void FitAvailable(Window window, double width, double height)
    {
        if (IsFullscreen(window)) return;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 320 || height < 240) return;
        var availableWidth = Math.Max(320, width - 24); var availableHeight = Math.Max(240, height - 24);
        window.MinWidth = Math.Min(window.MinWidth, availableWidth); window.MinHeight = Math.Min(window.MinHeight, availableHeight);
        if (window.WindowState == WindowState.Normal)
        {
            window.Width = Math.Min(window.Width, availableWidth); window.Height = Math.Min(window.Height, availableHeight);
        }
    }

    internal static bool IsFullscreen(Window window)
    {
        lock (FullscreenWindows) return FullscreenWindows.Contains(window);
    }

    internal static void SetFullscreen(Window window, bool fullscreen)
    {
        lock (FullscreenWindows)
        {
            if (fullscreen) FullscreenWindows.Add(window);
            else FullscreenWindows.Remove(window);
        }
    }

    /// <summary>
    /// Captures where the window should return when fullscreen ends. Even when the window is
    /// maximised this returns the normal placement, so un-maximising later still works.
    /// </summary>
    internal static Placement CaptureRestorePlacement(Window window)
    {
        var fallback = new Placement(
            (int)Math.Round(window.Left), (int)Math.Round(window.Top),
            Math.Max(1, (int)Math.Round(window.Width)), Math.Max(1, (int)Math.Round(window.Height)));

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return fallback;

        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(handle, ref placement)) return fallback;

        var normal = placement.NormalPosition;
        return new Placement(
            normal.Left, normal.Top,
            Math.Max(1, normal.Right - normal.Left), Math.Max(1, normal.Bottom - normal.Top));
    }

    /// <summary>
    /// Stretches the window over the whole monitor instead of the work area, which is what
    /// maximising does, so the taskbar area is covered too.
    /// </summary>
    internal static void CoverMonitor(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, MonitorDefaultToNearest), ref info)) return;

        var monitor = info.Monitor;
        SetWindowPos(handle, IntPtr.Zero,
            monitor.Left, monitor.Top,
            Math.Max(1, monitor.Right - monitor.Left), Math.Max(1, monitor.Bottom - monitor.Top),
            SwpNoZOrder | SwpNoOwnerZOrder | SwpFrameChanged | SwpShowWindow);
    }

    internal static void RestorePlacement(Window window, Placement placement)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        SetWindowPos(handle, IntPtr.Zero,
            placement.Left, placement.Top, placement.Width, placement.Height,
            SwpNoZOrder | SwpNoOwnerZOrder | SwpFrameChanged);
    }

    /// <summary>
    /// Tells the shell this window owns the screen so the taskbar is pushed to the bottom of
    /// the z-order while the window is active.
    /// </summary>
    internal static void MarkFullscreen(Window window, bool fullscreen)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        try { Taskbar?.MarkFullscreenWindow(handle, fullscreen); }
        catch (Exception) { /* shell not available: Topmost still covers the taskbar */ }
    }

    private static ITaskbarList2? Taskbar
    {
        get
        {
            if (_taskbar is not null) return _taskbar;
            try
            {
                var list = (ITaskbarList2)new TaskbarList();
                list.HrInit();
                _taskbar = list;
            }
            catch (Exception) { _taskbar = null; }
            return _taskbar;
        }
    }
}
