using CefSharp.Wpf;

namespace CrimsonBrowser.Models;

public sealed class CefBrowserView : ChromiumWebBrowser
{
    public double ZoomFactor
    {
        get => Math.Pow(1.2, ZoomLevel);
        set => ZoomLevel = Math.Log(Math.Clamp(value, 0.25, 3.0), 1.2);
    }
}
