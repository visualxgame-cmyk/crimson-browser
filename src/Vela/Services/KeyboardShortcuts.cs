using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Vela.Services;

/// <summary>
/// WebView2 renders the page in a child window, so key presses made while the page has focus
/// (for example inside a text field) never reach WPF: Ctrl+T, Ctrl+W and friends would be dead.
/// A low level keyboard hook gives Vela first look at the key exactly like a real browser does,
/// and only while one of Vela's own windows is in the foreground.
/// </summary>
internal static class KeyboardShortcuts
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const uint GaRoot = 2;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vk);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    private static readonly Dictionary<IntPtr, Func<Key, ModifierKeys, bool>> Handlers = new();

    private static HookProc? _proc;
    private static IntPtr _hook;
    private static int _swallowedVk;

    public static void Register(IntPtr hwnd, Func<Key, ModifierKeys, bool> handler)
    {
        Handlers[hwnd] = handler;
        if (_hook != IntPtr.Zero) return;

        _proc = Callback;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write("Keyboard hook failed: " + Marshal.GetLastWin32Error());
    }

    public static void Unregister(IntPtr hwnd)
    {
        Handlers.Remove(hwnd);
        if (Handlers.Count > 0 || _hook == IntPtr.Zero) return;

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
    }

    private static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = (int)wParam;

            // Swallow the matching key up as well, so pages never see a half pressed key.
            if (message is WmKeyUp or WmSysKeyUp && _swallowedVk != 0)
            {
                var upForeground = GetAncestor(GetForegroundWindow(), GaRoot);
                if (Handlers.ContainsKey(upForeground) && Marshal.ReadInt32(lParam) == _swallowedVk)
                {
                    _swallowedVk = 0;
                    return 1;
                }
            }

            if (message is WmKeyDown or WmSysKeyDown && Handlers.Count > 0)
            {
                // While a page has focus the foreground window can be WebView2's own child
                // window, so resolve the top level window it belongs to.
                var foreground = GetAncestor(GetForegroundWindow(), GaRoot);
                if (foreground != IntPtr.Zero && Handlers.TryGetValue(foreground, out var handler))
                {
                    var vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
                    var key = KeyInterop.KeyFromVirtualKey(vk);
                    var modifiers = CurrentModifiers();

                    try
                    {
                        if (handler(key, modifiers))
                        {
                            _swallowedVk = vk;
                            return 1;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Never let a shortcut break global keyboard input.
                        Log.Write("Shortcut: " + ex.Message);
                    }
                }
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static ModifierKeys CurrentModifiers()
    {
        var modifiers = ModifierKeys.None;
        if ((GetKeyState(VkControl) & 0x8000) != 0) modifiers |= ModifierKeys.Control;
        if ((GetKeyState(VkShift) & 0x8000) != 0) modifiers |= ModifierKeys.Shift;
        if ((GetKeyState(VkMenu) & 0x8000) != 0) modifiers |= ModifierKeys.Alt;
        return modifiers;
    }
}
