using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DropWheel;

// A small card in the bottom right corner: progress while a job runs, the
// result with buttons when it is done.
internal sealed class ToastWindow : Window
{
    readonly TextBlock _title;
    readonly TextBlock _message;
    readonly ProgressBar _progress;
    readonly StackPanel _buttons;
    readonly Border _card;
    readonly DispatcherTimer _hideTimer;

    public ToastWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        Title = "DropWheel";

        _title = new TextBlock
        {
            FontFamily = Theme.Text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(0xFF, 0xF4, 0xF4, 0xF6),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _message = new TextBlock
        {
            FontFamily = Theme.Text,
            FontSize = 12.5,
            Foreground = Theme.Brush(0xC8, 0xF4, 0xF4, 0xF6),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            MaxHeight = 120,
        };
        _progress = new ProgressBar
        {
            Height = 4,
            Minimum = 0,
            Maximum = 1,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = Theme.Brush(Theme.Accent),
            Background = Theme.Brush(0x30, 0xFF, 0xFF, 0xFF),
            BorderThickness = new Thickness(0),
        };
        _buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var close = new TextBlock
        {
            Text = "",
            FontFamily = Theme.Icons,
            FontSize = 11,
            Foreground = Theme.Brush(0x90, 0xF4, 0xF4, 0xF6),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        close.MouseLeftButtonUp += (_, _) => Hide();

        var layout = new Grid();
        layout.Children.Add(new StackPanel { Children = { _title, _message, _progress, _buttons } });
        layout.Children.Add(close);

        _card = new Border
        {
            Background = Theme.Brush(0xF2, 0x20, 0x22, 0x27),
            BorderBrush = Theme.Brush(0x40, 0xFF, 0xFF, 0xFF),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 14, 14),
            Margin = new Thickness(12),
            Child = layout,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Opacity = 0.45,
            },
        };
        Content = _card;

        _hideTimer = new DispatcherTimer();
        _hideTimer.Tick += (_, _) =>
        {
            if (IsMouseOver)
            {
                return;
            }
            _hideTimer.Stop();
            Hide();
        };

        SourceInitialized += (_, _) => Native.MakeNoActivate(this);
        SizeChanged += (_, _) => PlaceInCorner();
    }

    void PlaceInCorner()
    {
        Rect work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth;
        Top = work.Bottom - ActualHeight;
    }

    void Present()
    {
        bool wasVisible = IsVisible;
        if (!wasVisible)
        {
            Show();
        }
        PlaceInCorner();
        if (!wasVisible)
        {
            _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        }
    }

    public void ShowProgress(string title, string message, double? progress)
    {
        _hideTimer.Stop();
        _title.Text = title;
        _message.Text = message;
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = progress == null;
        if (progress != null)
        {
            _progress.Value = progress.Value;
        }
        _buttons.Children.Clear();
        _buttons.Visibility = Visibility.Collapsed;
        Present();
    }

    public void ShowMessage(string title, string message, IEnumerable<(string label, Action action)>? buttons = null,
        double seconds = 6)
    {
        _title.Text = title;
        _message.Text = message;
        _progress.Visibility = Visibility.Collapsed;
        _buttons.Children.Clear();
        if (buttons != null)
        {
            foreach (var (label, action) in buttons)
            {
                _buttons.Children.Add(MakeButton(label, action));
            }
        }
        _buttons.Visibility = _buttons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Present();

        _hideTimer.Stop();
        if (seconds > 0)
        {
            _hideTimer.Interval = TimeSpan.FromSeconds(seconds);
            _hideTimer.Start();
        }
    }

    Border MakeButton(string label, Action action)
    {
        var normal = Theme.Brush(0x30, 0xFF, 0xFF, 0xFF);
        var hover = Theme.Brush(Theme.Accent);
        var button = new Border
        {
            Background = normal,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 5, 12, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = label,
                FontFamily = Theme.Text,
                FontSize = 12.5,
                Foreground = Theme.Brush(0xFF, 0xF4, 0xF4, 0xF6),
            },
        };
        button.MouseEnter += (_, _) => button.Background = hover;
        button.MouseLeave += (_, _) => button.Background = normal;
        button.MouseLeftButtonUp += (_, _) =>
        {
            Hide();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Write("Toast action failed: " + ex);
            }
        };
        return button;
    }
}
