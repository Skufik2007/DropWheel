using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace DropWheel;

internal static class Theme
{
    public static Color Accent { get; } = ReadAccent();

    static Color ReadAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
            }
        }
        catch
        {
        }
        return Color.FromRgb(0x4C, 0xC2, 0xFF);
    }

    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => Brush(Color.FromArgb(a, r, g, b));
}

// The radial menu. It shows up under the cursor during a file drag, and the
// file is dropped onto one of its sectors.
//
// Kept fast: the window is never hidden, only parked off screen fully
// transparent, so showing it is a move and a fade instead of creating a
// layered window surface. Each ring of sectors is built once per file type
// and mode and reused; the center only gets its texts changed.
internal sealed class WheelWindow : Window
{
    const double WheelSize = 460;
    const double Outer = 206;
    const double Inner = 68;
    const double Center = WheelSize / 2;
    const double GapDegrees = 1.4;
    const int Parked = -20000;
    const int MaxRings = 40;

    sealed class Sector
    {
        public required System.Windows.Shapes.Path Shape { get; init; }
        public required ScaleTransform Scale { get; init; }
    }

    sealed class Ring
    {
        public required Canvas Canvas { get; init; }
        public required List<Sector> Sectors { get; init; }
        public required List<WheelOption> Options { get; init; }
    }

    readonly Grid _root;
    readonly Border _ringHost = new();
    readonly ScaleTransform _zoom = new(1, 1, Center, Center);
    readonly RotateTransform _spin = new(0, Center, Center);
    readonly Dictionary<string, Ring> _rings = new();
    Ring? _ring;

    readonly Ellipse _centerDisc;
    readonly TextBlock _modeTitle;
    readonly TextBlock _fileLine;
    readonly TextBlock _hint;
    readonly Ellipse[] _dots;

    WheelMode _mode;
    IReadOnlyList<string> _files = Array.Empty<string>();
    List<WheelOption> _options = new();
    int _hover = -1;
    bool _hoverCenter;

    static readonly Brush SectorFill = Theme.Brush(0xEC, 0x20, 0x22, 0x27);
    static readonly Brush SectorStroke = Theme.Brush(0x50, 0xFF, 0xFF, 0xFF);
    static readonly Brush CenterFill = Theme.Brush(0xF5, 0x16, 0x17, 0x1B);
    static readonly Brush CancelFill = Theme.Brush(0xF5, 0x6E, 0x1E, 0x26);
    static readonly Brush HoverFill = Theme.Brush(Theme.Accent);
    static readonly Brush AccentBrush = Theme.Brush(Theme.Accent);
    static readonly Brush TextBrush = Theme.Brush(0xFF, 0xF4, 0xF4, 0xF6);
    static readonly Brush Dim = Theme.Brush(0xB0, 0xF4, 0xF4, 0xF6);
    static readonly Brush HitCatcher = Theme.Brush(0x02, 0, 0, 0);

    // Animations are made once and frozen: nothing is allocated per frame or
    // per hover, so the garbage collector doesn't interrupt them.
    static readonly IEasingFunction PopEase = Frozen(new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut });
    static readonly IEasingFunction SpinEase = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    static readonly DoubleAnimation ZoomFromSmall = Frozen(new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = PopEase });
    static readonly DoubleAnimation ZoomFromNear = Frozen(new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = PopEase });
    // From 0.3, not 0: the wheel is there on the very first frame.
    static readonly DoubleAnimation FadeIn = Frozen(new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(110)));
    static readonly DoubleAnimation SpinForward = Frozen(new DoubleAnimation(-40, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = SpinEase });
    static readonly DoubleAnimation SpinBack = Frozen(new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = SpinEase });
    static readonly DoubleAnimation HoverOn = Frozen(new DoubleAnimation(1.045, TimeSpan.FromMilliseconds(110)));
    static readonly DoubleAnimation HoverOff = Frozen(new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));

    static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    public bool DropHappened { get; private set; }

    // Whether the wheel is on screen. The window itself stays shown.
    public bool IsOpen { get; private set; }

    public event Action? OpenChanged;
    public event Action<WheelOption, IReadOnlyList<string>>? OptionChosen;

    public WheelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = Height = WheelSize;
        AllowDrop = true;
        Title = "DropWheel";
        Left = Top = Parked;

        // A nearly invisible disc keeps the whole wheel a drop target: fully
        // transparent pixels of a layered window let the drag through.
        var catcher = new Ellipse
        {
            Width = Outer * 2 + 40,
            Height = Outer * 2 + 40,
            Fill = HitCatcher,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        double radius = Inner - 5;
        _centerDisc = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = CenterFill,
            Stroke = SectorStroke,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _modeTitle = new TextBlock
        {
            FontFamily = Theme.Text,
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = AccentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _fileLine = new TextBlock
        {
            FontFamily = Theme.Text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 3),
        };
        _hint = new TextBlock
        {
            FontFamily = Theme.Text,
            FontSize = 10,
            Foreground = Dim,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // Three dots, the current mode lit: the mouse wheel moves between them.
        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        _dots = Enum.GetValues<WheelMode>().Select(_ => new Ellipse
        {
            Height = 6,
            Margin = new Thickness(3, 0, 3, 0),
        }).ToArray();
        foreach (var dot in _dots)
        {
            dots.Children.Add(dot);
        }

        var centerText = new StackPanel
        {
            IsHitTestVisible = false,
            Width = radius * 2 - 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _modeTitle, _fileLine, _hint, dots },
        };

        _root = new Grid
        {
            Width = WheelSize,
            Height = WheelSize,
            Opacity = 0,
            RenderTransform = new TransformGroup { Children = { _spin, _zoom } },
            Children = { catcher, _ringHost, _centerDisc, centerText },
        };
        Content = _root;

        SourceInitialized += (_, _) => Native.MakeNoActivate(this);
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, _) => SetHover(-1, false);
        Drop += OnDrop;
    }

    // Builds the rings for the common file types and draws the window once
    // off screen, so the first drag after start is as quick as any other.
    public void Warmup()
    {
        foreach (string sample in new[] { "a.jpg", "a.png", "a.mp4", "a.mp3", "a.docx", "a.xlsx", "a.zip", "a.txt" })
        {
            var files = new[] { System.IO.Path.Combine(System.IO.Path.GetTempPath(), sample) };
            foreach (WheelMode mode in Enum.GetValues<WheelMode>())
            {
                RingFor(mode, files);
            }
        }
        _files = Array.Empty<string>();
        Rebuild();
        Park();
        if (!IsVisible)
        {
            Show();
        }
    }

    public void ShowAt(WheelMode mode)
    {
        _mode = mode;
        _files = Array.Empty<string>();
        DropHappened = false;
        Rebuild();
        if (!IsVisible)
        {
            Show();
        }
        PositionAtCursor();
        AnimateIn(ZoomFromSmall);
        SetOpen(true);
    }

    public void SwitchMode(WheelMode mode, int direction = 0)
    {
        if (mode == _mode)
        {
            return;
        }
        _mode = mode;
        Rebuild();
        AnimateIn(ZoomFromNear);
        if (direction != 0)
        {
            // The new ring comes in turning the way the mouse wheel went.
            _spin.BeginAnimation(RotateTransform.AngleProperty, direction > 0 ? SpinForward : SpinBack);
        }
    }

    // One step of the mouse wheel: convert -> tools -> actions -> convert.
    public void CycleMode(int step)
    {
        const int modes = 3;
        var next = (WheelMode)((((int)_mode + step) % modes + modes) % modes);
        SwitchMode(next, step);
    }

    public void HideWheel()
    {
        if (IsOpen)
        {
            Park();
        }
        _files = Array.Empty<string>();
        SetOpen(false);
    }

    void SetOpen(bool open)
    {
        if (IsOpen != open)
        {
            IsOpen = open;
            OpenChanged?.Invoke();
        }
    }

    // Off screen and fully transparent, ready to come back in one move.
    void Park()
    {
        _root.BeginAnimation(OpacityProperty, null);
        _root.Opacity = 0;
        SetHover(-1, false);
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, Parked, Parked, 0, 0, Native.SWP_NOACTIVATE | Native.SWP_NOSIZE);
    }

    void AnimateIn(DoubleAnimation zoom)
    {
        _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
        _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
        _root.BeginAnimation(OpacityProperty, FadeIn);
    }

    // Centered on the cursor, kept inside the monitor's work area. Physical
    // pixels, since the monitors may have different scaling.
    void PositionAtCursor()
    {
        Native.GetCursorPos(out var cursor);
        var (_, scale, work) = Native.MonitorAt(cursor);
        int size = (int)Math.Round(WheelSize * scale);
        int x = Math.Clamp(cursor.X - size / 2, work.Left, Math.Max(work.Left, work.Right - size));
        int y = Math.Clamp(cursor.Y - size / 2, work.Top, Math.Max(work.Top, work.Bottom - size));
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, size, size, Native.SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------- drawing

    static Point Polar(double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        return new Point(Center + radius * Math.Sin(radians), Center - radius * Math.Cos(radians));
    }

    static Geometry SectorGeometry(double from, double to)
    {
        bool large = to - from > 180;
        var figure = new PathFigure { StartPoint = Polar(Inner, from), IsClosed = true };
        figure.Segments.Add(new LineSegment(Polar(Outer, from), true));
        figure.Segments.Add(new ArcSegment(Polar(Outer, to), new Size(Outer, Outer), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(Polar(Inner, to), true));
        figure.Segments.Add(new ArcSegment(Polar(Inner, from), new Size(Inner, Inner), 0, large, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }

    void Rebuild()
    {
        SetHover(-1, false);
        _ring = RingFor(_mode, _files);
        _options = _ring.Options;
        if (_ringHost.Child != _ring.Canvas)
        {
            _ringHost.Child = _ring.Canvas;
        }
        UpdateCenter();
    }

    // The options depend only on the mode, the file type and extension,
    // whether there are several files and whether FFmpeg is there.
    Ring RingFor(WheelMode mode, IReadOnlyList<string> files)
    {
        string key = files.Count == 0
            ? $"{mode}|-"
            : $"{mode}|{Catalog.KindOf(files[0])}|{Catalog.ExtOf(files[0])}|{files.Count > 1}|{Ffmpeg.IsAvailable}";
        if (_rings.TryGetValue(key, out var ring))
        {
            return ring;
        }
        if (_rings.Count >= MaxRings)
        {
            _rings.Clear();
        }
        ring = BuildRing(Catalog.For(mode, files));
        _rings[key] = ring;
        return ring;
    }

    static Ring BuildRing(List<WheelOption> options)
    {
        var canvas = new Canvas { Width = WheelSize, Height = WheelSize };
        var sectors = new List<Sector>();
        int count = options.Count;
        double step = count > 0 ? 360.0 / count : 360;
        for (int i = 0; i < count; i++)
        {
            double middle = i * step;
            var scale = new ScaleTransform(1, 1, Center, Center);
            var shape = new System.Windows.Shapes.Path
            {
                Data = SectorGeometry(middle - step / 2 + GapDegrees / 2, middle + step / 2 - GapDegrees / 2),
                Fill = SectorFill,
                Stroke = SectorStroke,
                StrokeThickness = 1,
                RenderTransform = scale,
            };
            canvas.Children.Add(shape);
            sectors.Add(new Sector { Shape = shape, Scale = scale });
        }

        for (int i = 0; i < count; i++)
        {
            AddLabel(canvas, options[i], i * step, count);
        }
        return new Ring { Canvas = canvas, Sectors = sectors, Options = options };
    }

    static void AddLabel(Canvas canvas, WheelOption option, double degrees, int count)
    {
        var panel = new StackPanel { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center };
        if (option.Glyph.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = option.Glyph,
                FontFamily = Theme.Icons,
                FontSize = 20,
                Foreground = TextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 3),
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = option.Label,
            FontFamily = Theme.Text,
            FontSize = option.IsConversion ? 17 : (count > 6 ? 12.5 : 13.5),
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        });
        if (option.Sub.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = option.Sub,
                FontFamily = Theme.Text,
                FontSize = 11,
                Foreground = Dim,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        if (option.NeedsFfmpeg && !Ffmpeg.IsAvailable)
        {
            panel.Opacity = 0.55;
        }

        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Point at = Polar((Inner + Outer) / 2, degrees);
        Canvas.SetLeft(panel, at.X - panel.DesiredSize.Width / 2);
        Canvas.SetTop(panel, at.Y - panel.DesiredSize.Height / 2);
        canvas.Children.Add(panel);
    }

    void UpdateCenter()
    {
        string fileLine;
        if (_files.Count == 0)
        {
            fileLine = "…";
        }
        else
        {
            fileLine = System.IO.Path.GetFileName(_files[0].TrimEnd('\\', '/'));
            if (fileLine.Length > 16)
            {
                fileLine = fileLine[..15] + "…";
            }
            if (_files.Count > 1)
            {
                fileLine += $" +{_files.Count - 1}";
            }
        }

        _modeTitle.Text = Catalog.ModeTitle(_mode).ToUpperInvariant();
        _fileLine.Text = fileLine;
        _hint.Text = _files.Count > 0 && _options.Count == 0 ? "нет вариантов" : "сюда — отмена";
        for (int i = 0; i < _dots.Length; i++)
        {
            bool current = i == (int)_mode;
            _dots[i].Width = current ? 14 : 6;
            _dots[i].Fill = current ? AccentBrush : Dim;
            _dots[i].Opacity = current ? 1 : 0.5;
        }
    }

    // ------------------------------------------------------------ hit test

    (int sector, bool center) HitTest(Point p)
    {
        double dx = p.X - Center;
        double dy = p.Y - Center;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < Inner)
        {
            return (-1, true);
        }
        if (distance > Outer + 24 || _options.Count == 0)
        {
            return (-1, false);
        }
        double degrees = Math.Atan2(dx, -dy) * 180 / Math.PI;
        if (degrees < 0)
        {
            degrees += 360;
        }
        double step = 360.0 / _options.Count;
        int index = (int)Math.Floor(((degrees + step / 2) % 360) / step);
        return (Math.Clamp(index, 0, _options.Count - 1), false);
    }

    void SetHover(int index, bool center)
    {
        if (index == _hover && center == _hoverCenter)
        {
            return;
        }
        var sectors = _ring?.Sectors;
        if (sectors != null && _hover >= 0 && _hover < sectors.Count)
        {
            AnimateSector(sectors[_hover], false);
        }
        _hover = index;
        _hoverCenter = center;
        if (sectors != null && _hover >= 0 && _hover < sectors.Count)
        {
            AnimateSector(sectors[_hover], true);
        }
        _centerDisc.Fill = center ? CancelFill : CenterFill;
    }

    static void AnimateSector(Sector sector, bool on)
    {
        sector.Shape.Fill = on ? HoverFill : SectorFill;
        var animation = on ? HoverOn : HoverOff;
        sector.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        sector.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    // -------------------------------------------------------- drag and drop

    static IReadOnlyList<string> FilesOf(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            ? files
            : Array.Empty<string>();

    void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!IsOpen)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        var files = FilesOf(e.Data);
        if (files.Count > 0 && (_files.Count == 0 || !files.SequenceEqual(_files)))
        {
            _files = files;
            Rebuild();
        }
        OnDragOver(sender, e);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        var (sector, center) = HitTest(e.GetPosition(_root));
        SetHover(sector, center);
        // Always a copy: with Shift held Explorer would otherwise treat the
        // drop as a move and delete the original.
        e.Effects = IsOpen && sector >= 0 && _files.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (!IsOpen)
        {
            return;
        }
        DropHappened = true;

        var files = FilesOf(e.Data);
        var (sector, _) = HitTest(e.GetPosition(_root));
        WheelOption? option = sector >= 0 && sector < _options.Count ? _options[sector] : null;
        if (option != null && files.Count > 0)
        {
            e.Effects = DragDropEffects.Copy;
        }

        HideWheel();
        if (option != null && files.Count > 0)
        {
            OptionChosen?.Invoke(option, files);
        }
    }

    // ------------------------------------------------- preview for checking

    // For --bench: shows the wheel with files already known, off screen.
    internal void ShowForBench(WheelMode mode, IReadOnlyList<string> files)
    {
        _mode = mode;
        _files = files;
        DropHappened = false;
        Rebuild();
        if (!IsVisible)
        {
            Show();
        }
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, -4000, -4000, (int)WheelSize, (int)WheelSize, Native.SWP_NOACTIVATE);
        AnimateIn(ZoomFromSmall);
        SetOpen(true);
    }

    // Renders the wheel to a PNG without showing it, for the --render
    // command line switch.
    public void RenderToPng(WheelMode mode, IReadOnlyList<string> files, int hover, string path)
    {
        _mode = mode;
        _files = files;
        Rebuild();
        _root.BeginAnimation(OpacityProperty, null);
        _root.Opacity = 1;
        if (hover >= 0)
        {
            SetHover(hover, false);
            if (_ring != null && hover < _ring.Sectors.Count)
            {
                var scale = _ring.Sectors[hover].Scale;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = scale.ScaleY = 1.045;
            }
        }

        Content = null;
        var host = new Border
        {
            Width = WheelSize,
            Height = WheelSize,
            Background = new LinearGradientBrush(Color.FromRgb(0x35, 0x5C, 0x7D), Color.FromRgb(0x6C, 0x5B, 0x7B), 45),
            Child = _root,
        };
        host.Measure(new Size(WheelSize, WheelSize));
        host.Arrange(new Rect(0, 0, WheelSize, WheelSize));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)WheelSize * 2, (int)WheelSize * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);

        host.Child = null;
        Content = _root;
    }
}
