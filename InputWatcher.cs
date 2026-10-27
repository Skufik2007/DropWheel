using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace DropWheel;

// Watches the keyboard and the left mouse button system-wide. A modifier
// pressed while the left button drags something out of Explorer opens the
// wheel. The hooks live on their own thread with its own message loop: a
// busy UI thread must never hold up the input of the whole system.
internal sealed unsafe class InputWatcher : IDisposable
{
    const uint VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_ESCAPE = 0x1B;
    const uint VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    const int DragThreshold = 6;

    static readonly string[] ExplorerRootClasses =
    {
        "CabinetWClass", // File Explorer windows
        "ExploreWClass",
        "Progman", // the desktop
        "WorkerW",
    };

    readonly Dispatcher _ui;
    Thread? _thread;
    uint _threadId;
    readonly ManualResetEventSlim _started = new();

    // The hooks are static functions called straight from user32; they reach
    // the one watcher through this.
    static InputWatcher? _current;

    // For --hooktest: how many events each hook has seen.
    public static int KeyboardCalls;
    public static int MouseCalls;

    volatile bool _leftDown;
    Native.POINT _downPoint;
    volatile bool _downOnExplorer;
    readonly HashSet<uint> _held = new();

    int _wheelAccumulator;
    volatile bool _wheelVisible;

    public bool IsLeftButtonDown => _leftDown;

    // Set by the UI while the radial menu is on screen: the mouse wheel then
    // flips through its modes instead of scrolling whatever lies below.
    public bool WheelVisible
    {
        get => _wheelVisible;
        set
        {
            _wheelVisible = value;
            _wheelAccumulator = 0;
        }
    }

    public event Action<WheelMode>? DragModifierPressed;
    public event Action<int>? WheelScrolled;
    public event Action? LeftButtonReleased;
    public event Action? EscapePressed;

    public InputWatcher(Dispatcher ui)
    {
        _ui = ui;
    }

    public void Start()
    {
        _current = this;
        // Above everything else in the app: while a hook waits for us, the
        // mouse waits too.
        _thread = new Thread(HookThread)
        {
            IsBackground = true,
            Name = "DropWheel input hooks",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
    }

    void HookThread()
    {
        _threadId = Native.GetCurrentThreadId();
        IntPtr keyboardHook = IntPtr.Zero;
        IntPtr mouseHook = IntPtr.Zero;
        RefreshHooks(ref keyboardHook, ref mouseHook);
        _started.Set();

        // Windows silently drops a low-level hook that once answered too
        // slowly, e.g. while the machine was swamped, and nothing reports it.
        // Installing the hooks afresh now and then keeps the wheel working.
        UIntPtr timer = Native.SetTimer(IntPtr.Zero, UIntPtr.Zero, (uint)HookRefreshInterval.TotalMilliseconds, IntPtr.Zero);

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == Native.WM_TIMER && msg.hwnd == IntPtr.Zero)
            {
                RefreshHooks(ref keyboardHook, ref mouseHook);
            }
        }

        Native.KillTimer(IntPtr.Zero, timer);
        Native.UnhookWindowsHookEx(keyboardHook);
        Native.UnhookWindowsHookEx(mouseHook);
    }

    static readonly TimeSpan HookRefreshInterval = TimeSpan.FromMinutes(5);

    // The new hook goes in before the old one comes out, so no input is
    // missed in between.
    void RefreshHooks(ref IntPtr keyboardHook, ref IntPtr mouseHook)
    {
        IntPtr module = Native.GetModuleHandle(null);
        IntPtr newKeyboard = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, (IntPtr)(delegate* unmanaged<int, IntPtr, IntPtr, IntPtr>)&KeyboardHook, module, 0);
        IntPtr newMouse = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, (IntPtr)(delegate* unmanaged<int, IntPtr, IntPtr, IntPtr>)&MouseHook, module, 0);
        if (newKeyboard == IntPtr.Zero || newMouse == IntPtr.Zero)
        {
            Log.Write("Installing input hooks failed: " + Marshal.GetLastWin32Error());
        }
        if (newKeyboard != IntPtr.Zero)
        {
            if (keyboardHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(keyboardHook);
            }
            keyboardHook = newKeyboard;
        }
        if (newMouse != IntPtr.Zero)
        {
            if (mouseHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(mouseHook);
            }
            mouseHook = newMouse;
        }
    }

    WheelMode? ModeFor(uint vk)
    {
        string? name = vk switch
        {
            VK_SHIFT => "Shift",
            VK_CONTROL => "Ctrl",
            VK_MENU => "Alt",
            _ => null,
        };
        if (name == null)
        {
            return null;
        }
        var settings = Settings.Current;
        if (string.Equals(settings.ConvertKey, name, StringComparison.OrdinalIgnoreCase)) return WheelMode.Convert;
        if (string.Equals(settings.ToolsKey, name, StringComparison.OrdinalIgnoreCase)) return WheelMode.Tools;
        if (string.Equals(settings.ActionsKey, name, StringComparison.OrdinalIgnoreCase)) return WheelMode.Actions;
        return null;
    }

    static uint Normalize(uint vk) => vk switch
    {
        VK_LSHIFT or VK_RSHIFT => VK_SHIFT,
        VK_LCONTROL or VK_RCONTROL => VK_CONTROL,
        VK_LMENU or VK_RMENU => VK_MENU,
        _ => vk,
    };

    bool IsDragging()
    {
        if (!_leftDown)
        {
            return false;
        }
        if (Settings.Current.ExplorerOnly && !_downOnExplorer)
        {
            return false;
        }
        Native.GetCursorPos(out var now);
        return Math.Abs(now.X - _downPoint.X) >= DragThreshold || Math.Abs(now.Y - _downPoint.Y) >= DragThreshold;
    }

    IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = (Native.KBDLLHOOKSTRUCT*)lParam;
                int message = wParam.ToInt32();
                uint vk = Normalize(data->vkCode);
                if (message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)
                {
                    // Held keys repeat; only the first press counts.
                    if (_held.Add(vk))
                    {
                        if (vk == VK_ESCAPE)
                        {
                            _ui.BeginInvoke(() => EscapePressed?.Invoke());
                        }
                        else if (ModeFor(vk) is WheelMode mode && IsDragging())
                        {
                            _ui.BeginInvoke(() => DragModifierPressed?.Invoke(mode));
                        }
                    }
                }
                else if (message is Native.WM_KEYUP or Native.WM_SYSKEYUP)
                {
                    _held.Remove(vk);
                }
            }
            catch
            {
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int message = wParam.ToInt32();
            // Mouse moves are the bulk of the calls and return right away.
            if (message == Native.WM_LBUTTONDOWN)
            {
                var data = (Native.MSLLHOOKSTRUCT*)lParam;
                _downPoint = data->pt;
                IntPtr root = Native.GetAncestor(Native.WindowFromPoint(data->pt), Native.GA_ROOT);
                _downOnExplorer = Native.ClassIsOneOf(root, ExplorerRootClasses);
                _leftDown = true;
            }
            else if (message == Native.WM_LBUTTONUP)
            {
                _leftDown = false;
                _ui.BeginInvoke(() => LeftButtonReleased?.Invoke());
            }
            else if (message == Native.WM_MOUSEWHEEL && _wheelVisible && _leftDown)
            {
                var data = (Native.MSLLHOOKSTRUCT*)lParam;
                // Touchpads scroll in small steps: one mode per notch's worth.
                _wheelAccumulator += (short)(data->mouseData >> 16);
                while (Math.Abs(_wheelAccumulator) >= WheelDelta)
                {
                    // Down goes forward, like turning a dial.
                    int step = _wheelAccumulator > 0 ? -1 : 1;
                    _wheelAccumulator += step * WheelDelta;
                    _ui.BeginInvoke(() => WheelScrolled?.Invoke(step));
                }
                // Swallowed: Explorer would otherwise scroll, or resize its
                // icons with Ctrl held.
                return new IntPtr(1);
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    const int WheelDelta = 120;

    [UnmanagedCallersOnly]
    static IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        KeyboardCalls++;
        return _current is { } watcher ? watcher.KeyboardProc(nCode, wParam, lParam) : Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    static IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            MouseCalls++;
            return _current is { } watcher ? watcher.MouseProc(nCode, wParam, lParam) : Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
        catch
        {
            // Nothing may leave a function called from native code.
            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
    }

    public void Dispose()
    {
        if (_thread != null)
        {
            Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
            _thread = null;
        }
    }
}
