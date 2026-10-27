using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DropWheel;

// --hooktest <report>: installs the input hooks and sends a Shift tap and a
// one-pixel mouse move there and back, which change nothing, to see that both hooks fire.
internal static class HookTest
{
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    public static void Run(string report)
    {
        using var watcher = new InputWatcher(Dispatcher.CurrentDispatcher);
        watcher.Start();
        var inputs = new[]
        {
            new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = 0x10 } } },
            new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = 0x10, dwFlags = 2 } } },
            new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dx = 1, dwFlags = 1 } } },
            new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dx = -1, dwFlags = 1 } } },
        };
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        File.WriteAllText(report, $"sent {sent}, keyboard hook {InputWatcher.KeyboardCalls}, mouse hook {InputWatcher.MouseCalls}");
    }
}
