using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DropWheel;

public partial class App : Application
{
    Mutex? _singleInstance;
    InputWatcher? _watcher;
    WheelWindow? _wheel;
    ToastWindow? _toast;
    Tray? _tray;
    readonly SemaphoreSlim _jobs = new(1, 1);
    readonly CancellationTokenSource _exiting = new();
    bool _downloading;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Windows-1251 and the other ANSI code pages of old text files.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Settings.Load();

        bool restarted = e.Args.Contains("--restarted");
        if (e.Args.Length > 0 && e.Args[0].StartsWith("--", StringComparison.Ordinal) && !restarted)
        {
            int code = RunCommandLine(e.Args);
            Shutdown(code);
            return;
        }

        // No "has stopped working" dialog: it would keep a crashed instance
        // alive and the replacement waiting for it.
        Native.WerSetFlags(Native.WER_FAULT_REPORTING_NO_UI);

        // One instance. After a crash the replacement waits for the old
        // process to be gone; its mutex then comes back abandoned.
        _singleInstance = new Mutex(false, "DropWheel.SingleInstance");
        bool owned;
        try
        {
            owned = _singleInstance.WaitOne(restarted ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }
        if (!owned)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("Unhandled: " + args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write("Unobserved: " + args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Write("Fatal: " + args.ExceptionObject);
            RestartAfterCrash();
        };

        // The program may have been moved, or set up with an older version.
        System.Threading.Tasks.Task.Run(Autostart.Repair);

        // For checking the crash recovery: a background thread fails after
        // the given delay, once (the restarted instance doesn't).
        if (!restarted && int.TryParse(Environment.GetEnvironmentVariable("DROPWHEEL_CRASH_TEST_MS"), out int crashAfter))
        {
            new Thread(() =>
            {
                Thread.Sleep(crashAfter);
                throw new InvalidOperationException("crash test");
            }) { IsBackground = true }.Start();
        }

        _wheel = new WheelWindow();
        _wheel.OptionChosen += OnOptionChosen;
        _toast = new ToastWindow();
        _tray = new Tray(ShowHelp, () => _ = DownloadFfmpegAsync(), Shutdown);

        _watcher = new InputWatcher(Dispatcher);
        _watcher.DragModifierPressed += OnDragModifier;
        _watcher.LeftButtonReleased += OnLeftButtonReleased;
        _watcher.EscapePressed += () => _wheel.HideWheel();
        _watcher.WheelScrolled += step =>
        {
            if (_wheel.IsOpen)
            {
                _wheel.CycleMode(step);
            }
        };
        _wheel.OpenChanged += () => _watcher.WheelVisible = _wheel.IsOpen;
        _watcher.Start();

        // Once the app is idle after start: draw the wheel and the toast off
        // screen, so the first drag doesn't wait for windows and code to load.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                _wheel.Warmup();
                new System.Windows.Interop.WindowInteropHelper(_toast).EnsureHandle();
            }
            catch (Exception ex)
            {
                Log.Write("Warmup failed: " + ex.Message);
            }
        });

        Log.Write("Started");
        if (!File.Exists(Path.Combine(Settings.Folder, "welcomed")))
        {
            File.WriteAllText(Path.Combine(Settings.Folder, "welcomed"), "");
            ShowHelp();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance != null)
        {
            Log.Write("Exited");
        }
        _exiting.Cancel();
        _watcher?.Dispose();
        _tray?.Dispose();
        if (_singleInstance != null)
        {
            try
            {
                _singleInstance.ReleaseMutex();
            }
            catch
            {
            }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }

    // Brings the program back after a crash, but gives up after three crashes
    // in ten minutes rather than looping.
    static void RestartAfterCrash()
    {
        try
        {
            string file = Path.Combine(Settings.Folder, "restarts.txt");
            DateTime now = DateTime.UtcNow;
            var recent = new List<DateTime>();
            if (File.Exists(file))
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    if (DateTime.TryParse(line, null, System.Globalization.DateTimeStyles.RoundtripKind, out var time) &&
                        now - time < TimeSpan.FromMinutes(10))
                    {
                        recent.Add(time);
                    }
                }
            }
            if (recent.Count >= 3)
            {
                Log.Write("Crashed 3 times in 10 minutes, not restarting");
                return;
            }
            recent.Add(now);
            File.WriteAllLines(file, recent.Select(t => t.ToString("o")));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--restarted")
            {
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Log.Write("Restart after crash failed: " + ex.Message);
        }
    }

    void ShowHelp()
    {
        _toast?.ShowMessage("DropWheel работает",
            "Начните тащить файл из Проводника и зажмите клавишу:\n" +
            $"{Settings.Current.ConvertKey} — конвертировать, {Settings.Current.ToolsKey} — инструменты, " +
            $"{Settings.Current.ActionsKey} — действия.\nКолёсико мыши листает режимы, не отпуская файл. " +
            "Бросьте файл на нужный сектор, в центр — отмена.",
            seconds: 12);
    }

    // ----------------------------------------------------------------- wheel

    void OnDragModifier(WheelMode mode)
    {
        if (_wheel == null)
        {
            return;
        }
        if (_wheel.IsOpen)
        {
            _wheel.SwitchMode(mode);
        }
        else
        {
            _wheel.ShowAt(mode);
        }
    }

    // The drag ended. The drop itself arrives a moment after the button
    // goes up, so the wheel waits a little before treating it as a cancel.
    void OnLeftButtonReleased()
    {
        if (_wheel == null || !_wheel.IsOpen)
        {
            return;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_wheel.IsOpen && !_wheel.DropHappened && _watcher?.IsLeftButtonDown != true)
            {
                _wheel.HideWheel();
            }
        };
        timer.Start();
    }

    void OnOptionChosen(WheelOption option, IReadOnlyList<string> files)
    {
        if (option.IsAction)
        {
            try
            {
                string? message = FileActions.Run(option, files);
                if (message != null)
                {
                    _toast?.ShowMessage(message, files.Count == 1 ? Path.GetFileName(files[0]) : "", seconds: 3);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"{option.Id} failed: {ex}");
                _toast?.ShowMessage("Не получилось", ex.Message);
            }
            return;
        }

        _ = RunJobAsync(option, files);
    }

    async Task RunJobAsync(WheelOption option, IReadOnlyList<string> files)
    {
        var toast = _toast!;
        if (option.NeedsFfmpeg && !Ffmpeg.IsAvailable)
        {
            toast.ShowMessage("Для этого нужен FFmpeg",
                "Видео, звук, WebP и AVIF обрабатывает FFmpeg — бесплатный конвертер. " +
                "Его нужно скачать один раз, около 110 МБ.",
                new (string, Action)[] { ("Скачать", () => _ = DownloadFfmpegAsync()) }, seconds: 0);
            return;
        }

        string title = option.Sub.Length > 0 && !option.IsConversion ? $"{option.Label} {option.Sub}" : option.Label;
        if (option.IsConversion)
        {
            title = "В " + title;
        }

        await _jobs.WaitAsync();
        try
        {
            toast.ShowProgress(title, "подготовка…", null);
            var result = await Processor.RunAsync(option, files,
                (fraction, name) => Dispatcher.BeginInvoke(() =>
                    toast.ShowProgress(title, name, fraction > 0 ? fraction : null)),
                _exiting.Token);
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write($"{option.Id} failed: {ex}");
            toast.ShowMessage("Не получилось", ex.Message, seconds: 10);
        }
        finally
        {
            _jobs.Release();
        }
    }

    void ShowResult(JobResult result)
    {
        var toast = _toast!;
        var notes = new StringBuilder();
        if (result.Outputs.Count == 1)
        {
            notes.Append(Path.GetFileName(result.Outputs[0]));
        }
        else if (result.Outputs.Count > 1)
        {
            notes.Append($"Готовых файлов: {result.Outputs.Count}");
        }
        if (result.Skipped > 0)
        {
            notes.Append($"\nПропущено файлов другого типа: {result.Skipped}");
        }
        foreach (string error in result.Errors.Take(3))
        {
            notes.Append('\n').Append(error);
        }

        if (result.Outputs.Count == 0)
        {
            toast.ShowMessage("Не получилось", notes.ToString().Trim(), seconds: 12);
            return;
        }

        string first = result.Outputs[0];
        var buttons = new List<(string, Action)> { ("Показать", () => FileActions.Reveal(first)) };
        if (result.Outputs.Count == 1)
        {
            buttons.Add(("Открыть", () => FileActions.Open(first)));
        }
        toast.ShowMessage(result.Errors.Count > 0 ? "Готово, но не всё" : "Готово", notes.ToString().Trim(),
            buttons, seconds: result.Errors.Count > 0 ? 12 : 6);
    }

    async Task DownloadFfmpegAsync()
    {
        if (_downloading || _toast == null)
        {
            return;
        }
        _downloading = true;
        var toast = _toast;
        try
        {
            toast.ShowProgress("Скачиваю FFmpeg", "сборка gyan.dev, около 110 МБ", 0);
            await Task.Run(() => Ffmpeg.DownloadAsync(
                p => Dispatcher.BeginInvoke(() => toast.ShowProgress("Скачиваю FFmpeg", $"{p:P0}", p)),
                _exiting.Token));
            toast.ShowMessage("FFmpeg установлен", "Теперь работают видео, звук, WebP и AVIF.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write("FFmpeg download failed: " + ex);
            toast.ShowMessage("Не удалось скачать FFmpeg", ex.Message, seconds: 12);
        }
        finally
        {
            _downloading = false;
        }
    }

    // ---------------------------------------------------------- command line

    // For checking the app without dragging anything:
    //   --run <option id> --report <file> <files...>
    //   --render <convert|tools|actions> <out.png> [--hover N] <files...>
    int RunCommandLine(string[] args)
    {
        try
        {
            if (args[0] == "--run")
            {
                string id = args[1];
                string report = args[3];
                var files = args.Skip(4).Select(Path.GetFullPath).ToList();
                FileKind kind = Catalog.KindOf(files[0]);
                var option = Catalog.All().First(o => o.Id == id && (o.Kind == null || o.Kind == kind));
                var result = Task.Run(() => Processor.RunAsync(option, files, (_, _) => { }, CancellationToken.None))
                    .GetAwaiter().GetResult();
                var lines = new List<string>();
                lines.AddRange(result.Outputs.Select(o => "OK " + o));
                lines.AddRange(result.Errors.Select(err => "ERR " + err));
                lines.Add("SKIPPED " + result.Skipped);
                File.WriteAllLines(report, lines);
                return result.Errors.Count == 0 ? 0 : 1;
            }

            if (args[0] == "--hooktest")
            {
                HookTest.Run(args[1]);
                return 0;
            }

            if (args[0] == "--bench")
            {
                Bench.Run(args[1]);
                return 0;
            }

            if (args[0] == "--download-ffmpeg")
            {
                Task.Run(() => Ffmpeg.DownloadAsync(_ => { }, CancellationToken.None)).GetAwaiter().GetResult();
                return Ffmpeg.IsAvailable ? 0 : 1;
            }

            if (args[0] == "--render")
            {
                var mode = Enum.Parse<WheelMode>(args[1], ignoreCase: true);
                string output = args[2];
                int hover = -1;
                int rest = 3;
                if (args.Length > 4 && args[3] == "--hover")
                {
                    hover = int.Parse(args[4]);
                    rest = 5;
                }
                var files = args.Skip(rest).Select(Path.GetFullPath).ToList();
                new WheelWindow().RenderToPng(mode, files, hover, output);
                return 0;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Command line failed: " + ex);
            return 2;
        }
        return 3;
    }
}
