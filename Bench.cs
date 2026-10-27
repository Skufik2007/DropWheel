using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;

namespace DropWheel;

// --bench <report>: how fast the wheel shows up, animates and switches modes.
// The wheel is shown off screen, so nothing flashes on the desktop.
internal static class Bench
{
    public static void Run(string report)
    {
        var lines = new List<string>();
        var files = new[] { @"C:\bench\photo.jpg" };
        var frames = new List<double>();
        var clock = Stopwatch.StartNew();
        EventHandler onFrame = (_, _) => frames.Add(clock.Elapsed.TotalMilliseconds);
        CompositionTarget.Rendering += onFrame;

        // First wheel after start: the app warms the wheel up at start, the
        // way App does it.
        var sw = Stopwatch.StartNew();
        var wheel = new WheelWindow();
        wheel.Warmup();
        WaitFrames(1);
        lines.Add($"warmup at start: {sw.Elapsed.TotalMilliseconds:0.0} ms (in the background, once)");
        Pump(300);
        sw.Restart();
        wheel.ShowForBench(WheelMode.Convert, files);
        double shown = sw.Elapsed.TotalMilliseconds;
        frames.Clear();
        WaitFrames(1);
        lines.Add($"cold show: {shown:0.0} ms to Show(), {sw.Elapsed.TotalMilliseconds:0.0} ms to first frame");
        Pump(400);
        lines.Add("cold animation: " + FrameStats(frames));
        wheel.HideWheel();
        Pump(100);

        // Warm: the same again, the way every later drag looks.
        var warm = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            sw.Restart();
            wheel.ShowForBench((WheelMode)(i % 3), files);
            frames.Clear();
            WaitFrames(1);
            warm.Add(sw.Elapsed.TotalMilliseconds);
            if (i == 4)
            {
                frames.Clear();
                Pump(400);
                lines.Add("warm animation: " + FrameStats(frames));
            }
            else
            {
                Pump(250);
            }
            wheel.HideWheel();
            Pump(50);
        }
        lines.Add($"warm show to first frame: avg {warm.Average():0.0} ms, max {warm.Max():0.0} ms");

        // Mouse wheel flips through the modes.
        wheel.ShowForBench(WheelMode.Convert, files);
        Pump(300);
        var switches = new List<double>();
        for (int i = 0; i < 30; i++)
        {
            sw.Restart();
            wheel.CycleMode(1);
            frames.Clear();
            WaitFrames(1);
            switches.Add(sw.Elapsed.TotalMilliseconds);
            Pump(30);
        }
        lines.Add($"mode switch to first frame: avg {switches.Average():0.0} ms, max {switches.Max():0.0} ms");
        frames.Clear();
        wheel.CycleMode(1);
        Pump(400);
        lines.Add("switch animation: " + FrameStats(frames));
        wheel.HideWheel();

        CompositionTarget.Rendering -= onFrame;
        var process = Process.GetCurrentProcess();
        process.Refresh();
        lines.Add($"memory: private {process.PrivateMemorySize64 / 1048576} MB, working set {process.WorkingSet64 / 1048576} MB");
        File.WriteAllLines(report, lines);
    }

    static string FrameStats(List<double> frames)
    {
        if (frames.Count < 2)
        {
            return "no frames";
        }
        // Only while the animation runs (~220 ms); WPF slows down after it.
        frames = frames.Where(f => f - frames[0] <= 230).ToList();
        var gaps = frames.Zip(frames.Skip(1), (a, b) => b - a).ToList();
        double span = frames[^1] - frames[0];
        string slow = string.Join(" ", gaps.Select((g, i) => (g, i)).Where(x => x.g > 12).Select(x => $"#{x.i}:{x.g:0}"));
        return $"{frames.Count} frames, {frames.Count / (span / 1000):0} fps, longest gap {gaps.Max():0.0} ms, slow [{slow}]";
    }

    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    // Waits until WPF has gone through the given number of frames since the
    // list was cleared, by pumping messages in small steps.
    static void WaitFrames(int count)
    {
        var frame = new DispatcherFrame();
        int seen = 0;
        EventHandler handler = null!;
        handler = (_, _) =>
        {
            if (++seen > count)
            {
                CompositionTarget.Rendering -= handler;
                frame.Continue = false;
            }
        };
        CompositionTarget.Rendering += handler;
        Dispatcher.PushFrame(frame);
    }
}
