using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace Vela.Services;

internal static class WindowPlacementStore
{
    static WindowPlacementStore() { }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Placement
    { public int Length; public int Flags; public int ShowCommand; public Point Minimum; public Point Maximum; public Rect Normal; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }
    private static readonly string FilePath = Path.Combine(AppPaths.Data, "window-placement.json");
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr window, in Placement placement);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(in Rect rect, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    public static void Restore(Window window)
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var saved = JsonSerializer.Deserialize<Placement>(File.ReadAllText(FilePath), new JsonSerializerOptions { IncludeFields = true });
            if (saved.Normal.Right - saved.Normal.Left < 300 || saved.Normal.Bottom - saved.Normal.Top < 200) return;
            var monitor = MonitorFromRect(saved.Normal, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var bounds = saved.Normal;
                if (bounds.Right <= info.Work.Left + 40 || bounds.Left >= info.Work.Right - 40 || bounds.Bottom <= info.Work.Top + 40 || bounds.Top >= info.Work.Bottom - 40)
                {
                    var width = Math.Min(bounds.Right - bounds.Left, info.Work.Right - info.Work.Left);
                    var height = Math.Min(bounds.Bottom - bounds.Top, info.Work.Bottom - info.Work.Top);
                    saved.Normal = new Rect { Left = info.Work.Left + 20, Top = info.Work.Top + 20, Right = info.Work.Left + width, Bottom = info.Work.Top + height };
                }
            }
            saved.Length = Marshal.SizeOf<Placement>(); saved.Flags = 0;
            saved.ShowCommand = saved.ShowCommand == 3 ? 3 : 1;
            SetWindowPlacement(new WindowInteropHelper(window).Handle, saved);
        }
        catch (Exception ex) { Log.Write("Restore window placement: " + ex.Message); }
    }
    public static void Save(Window window)
    {
        var value = new Placement { Length = Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(new WindowInteropHelper(window).Handle, ref value)) return;
        value.ShowCommand = window.WindowState == WindowState.Maximized || (value.Flags & 2) != 0 ? 3 : 1;
        try { AtomicFile.Write(FilePath, JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true })); }
        catch (Exception ex) { Log.Write("Save window placement: " + ex.Message); }
    }
}
