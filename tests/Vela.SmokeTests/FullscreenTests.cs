using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Vela.Models;
using Vela.Services;

namespace Vela.Tests;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    private static NativeRect WindowRectOf(Window window) =>
        GetWindowRect(new WindowInteropHelper(window).Handle, out var rect) ? rect : default;

    /// <summary>Captures the primary screen so fullscreen coverage can be inspected visually.</summary>
    private static void CaptureScreen(string path)
    {
        var width = GetSystemMetrics(0);
        var height = GetSystemMetrics(1);
        if (width <= 0 || height <= 0) return;

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, width, height, screen, 0, 0, 0x00CC0020);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = File.Create(path);
            encoder.Save(output);
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>True when the native window covers its monitor rect, taskbar area included.</summary>
    private static bool CoversWholeMonitor(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;

        var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) return false;
        if (!GetWindowRect(handle, out var rect)) return false;

        return rect.Left <= info.Monitor.Left + 1 && rect.Top <= info.Monitor.Top + 1 &&
               rect.Right >= info.Monitor.Right - 1 && rect.Bottom >= info.Monitor.Bottom - 1;
    }

    private static bool SameRect(NativeRect a, NativeRect b) =>
        Math.Abs(a.Left - b.Left) <= 2 && Math.Abs(a.Top - b.Top) <= 2 &&
        Math.Abs(a.Right - b.Right) <= 2 && Math.Abs(a.Bottom - b.Bottom) <= 2;

    private static async Task FullscreenTests()
    {
        var window = new MainWindow { WindowState = WindowState.Normal, Width = 960, Height = 640 };
        window.Show();
        try
        {
            var tabs = Field<ObservableCollection<BrowserTab>>(window, "_tabs");
            await Until(() => tabs.FirstOrDefault()?.View?.CoreWebView2 is not null, "Fullscreen fixture engine starts", 30000);
            await (Task)Invoke(window, "WaitForViewInitializationAsync")!;
            var core = tabs[0].View!.CoreWebView2;
            File.WriteAllText(Path.Combine(AppPaths.Assets, "fullscreen-test.html"), "<!doctype html><title>Fullscreen fixture</title><p>Local visual fixture.</p>");
            core.SetVirtualHostNameToFolderMapping("fullscreen.test", AppPaths.Assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate("https://fullscreen.test/fullscreen-test.html");
            await ScriptUntil(core, "location.hostname==='fullscreen.test' && document.readyState==='complete'", "Fullscreen fixture loads locally");

            var placementBefore = WindowRectOf(window);

            await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
            {
                expression = "document.documentElement.requestFullscreen()",
                awaitPromise = true,
                userGesture = true
            }));
            await Until(() => Field<bool>(window, "_isFullscreen"), "Page fullscreen enters the browser window");
            var rows = Field<Grid>(window, "RootGrid").RowDefinitions;
            Check(window.WindowState == WindowState.Normal && window.Topmost && rows[0].Height.Value == 0 && rows[1].Height.Value == 0 && rows[2].Height.Value == 0 && rows[3].Height.IsStar && rows[4].Height.Value == 0,
                "Fullscreen leaves the WebView content row visible and hides browser chrome");
            Check(CoversWholeMonitor(window),
                "Fullscreen covers the entire monitor rect, so the taskbar area is hidden");

            var preview = Path.Combine(AppPaths.Root, "fullscreen-preview.png");
            CaptureScreen(preview);
            Console.WriteLine("Fullscreen preview: " + preview);

            await core.ExecuteScriptAsync("document.exitFullscreen()");
            await Until(() => !Field<bool>(window, "_isFullscreen"), "Page fullscreen restores browser chrome after exit");
            Check(rows[0].Height.Value == 38 && rows[1].Height.IsAuto && rows[2].Height.IsAuto && rows[3].Height.IsStar && rows[4].Height.Value == 24,
                "Fullscreen exit restores the toolbar, permission bar slot and status bar");
            Check(!window.Topmost && window.WindowState == WindowState.Normal && SameRect(placementBefore, WindowRectOf(window)),
                "Fullscreen exit restores the previous window placement and clears topmost");

            var placementBeforeF11 = WindowRectOf(window);
            Invoke(window, "ToggleFullscreen");
            Check(Field<bool>(window, "_isFullscreen") && rows[3].Height.IsStar && CoversWholeMonitor(window),
                "F11 fullscreen keeps the WebView content row visible and covers the taskbar");
            Invoke(window, "ToggleFullscreen");
            Check(!Field<bool>(window, "_isFullscreen") && rows[4].Height.Value == 24 && !window.Topmost && SameRect(placementBeforeF11, WindowRectOf(window)),
                "Leaving F11 fullscreen restores browser chrome and the previous placement");
        }
        finally { await CloseWindowAsync(window); }
    }
}
