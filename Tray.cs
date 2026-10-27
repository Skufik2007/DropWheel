using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DropWheel;

internal sealed class Tray : IDisposable
{
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _ffmpegItem;

    public Tray(Action showHelp, Action downloadFfmpeg, Action exit)
    {
        var menu = new ContextMenuStrip();

        var header = new ToolStripMenuItem("DropWheel — тащите файл и жмите Shift / Ctrl / Alt") { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        var explorerOnly = new ToolStripMenuItem("Только при перетаскивании из Проводника")
        {
            Checked = Settings.Current.ExplorerOnly,
            CheckOnClick = true,
        };
        explorerOnly.CheckedChanged += (_, _) =>
        {
            Settings.Current.ExplorerOnly = explorerOnly.Checked;
            Settings.Save();
        };
        menu.Items.Add(explorerOnly);

        var autostart = new ToolStripMenuItem("Запускать вместе с Windows")
        {
            Checked = Autostart.IsEnabled,
            CheckOnClick = true,
        };
        autostart.CheckedChanged += (_, _) => Autostart.Set(autostart.Checked);
        menu.Items.Add(autostart);

        menu.Items.Add(new ToolStripSeparator());

        _ffmpegItem = new ToolStripMenuItem();
        _ffmpegItem.Click += (_, _) =>
        {
            if (!Ffmpeg.IsAvailable)
            {
                downloadFfmpeg();
            }
        };
        menu.Items.Add(_ffmpegItem);

        var openFolder = new ToolStripMenuItem("Открыть папку настроек и журнала");
        openFolder.Click += (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.Folder}\"") { UseShellExecute = true });
        menu.Items.Add(openFolder);

        var help = new ToolStripMenuItem("Как пользоваться");
        help.Click += (_, _) => showHelp();
        menu.Items.Add(help);

        menu.Items.Add(new ToolStripSeparator());
        var quit = new ToolStripMenuItem("Выход");
        quit.Click += (_, _) => exit();
        menu.Items.Add(quit);

        menu.Opening += (_, _) => UpdateFfmpegItem();
        UpdateFfmpegItem();

        _icon = new NotifyIcon
        {
            Icon = DrawIcon(),
            Text = "DropWheel",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                showHelp();
            }
        };
    }

    void UpdateFfmpegItem()
    {
        if (Ffmpeg.IsAvailable)
        {
            _ffmpegItem.Text = "FFmpeg найден — видео и звук работают";
            _ffmpegItem.Enabled = false;
        }
        else
        {
            _ffmpegItem.Text = "Скачать FFmpeg для видео и звука (~110 МБ)…";
            _ffmpegItem.Enabled = true;
        }
    }

    // A small wheel in the accent color.
    static Icon DrawIcon()
    {
        var accent = Theme.Accent;
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            for (int i = 0; i < 6; i++)
            {
                int alpha = i == 0 ? 255 : 150;
                using var brush = new SolidBrush(System.Drawing.Color.FromArgb(alpha, accent.R, accent.G, accent.B));
                g.FillPie(brush, 1, 1, 30, 30, -90 - 28 + i * 60, 54);
            }
            using var hole = new SolidBrush(System.Drawing.Color.FromArgb(255, 32, 34, 39));
            g.FillEllipse(hole, 10, 10, 12, 12);
        }
        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
