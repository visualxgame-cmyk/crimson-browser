using System.Reflection;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace Vela.Services;

/// <summary>Isolates the pinned WPF SDK's cached parent-window lifetime binding.</summary>
internal static class WebViewWindowLifetime
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo? BaseField = typeof(WebView2).GetField("m_webview2Base", InstanceMembers);
    private static readonly FieldInfo? ParentField = BaseField?.FieldType.GetField("_parentWindow", InstanceMembers);
    private static readonly MethodInfo? ClosedMethod = BaseField?.FieldType.GetMethod("ParentWindow_Closed", InstanceMembers);

    internal static void Retarget(WebView2 view, Window source, Window destination)
    {
        view.Dispatcher.VerifyAccess();
        // WebView2 1.0.4314.35 caches Window.GetWindow during initialization and subscribes
        // to that window's Closed event. Visual reparenting alone leaves a live tab tied to
        // its former window, which destroys it when that window closes. No public WPF API
        // updates this binding. Keep this compatibility bridge isolated and test it on every
        // SDK upgrade; reject unsupported SDK shapes before changing either visual tree.
        if (BaseField is null || ParentField is null || ClosedMethod is null ||
            BaseField.GetValue(view) is not { } lifetime || !ReferenceEquals(ParentField.GetValue(lifetime), source))
            throw new NotSupportedException("Versiunea WebView2 nu permite mutarea sigură a tabului live. Tabul a rămas în fereastra inițială.");
        var handler = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), lifetime, ClosedMethod);
        source.Closed -= handler;
        ParentField.SetValue(lifetime, destination);
        destination.Closed += handler;
    }
}
