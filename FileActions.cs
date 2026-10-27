using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DropWheel;

internal static class FileActions
{
    // Returns a short confirmation for the toast, or null when the action
    // speaks for itself (a window opens).
    public static string? Run(WheelOption option, IReadOnlyList<string> files)
    {
        switch (option.Id)
        {
            case "act:copypath":
                System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, files));
                return files.Count == 1 ? "Путь скопирован" : $"Скопировано путей: {files.Count}";

            case "act:copy":
                var list = new StringCollection();
                list.AddRange(files.ToArray());
                System.Windows.Clipboard.SetFileDropList(list);
                return files.Count == 1 ? "Файл в буфере обмена" : $"Файлов в буфере обмена: {files.Count}";

            case "act:float":
                foreach (string file in files.Take(6))
                {
                    new FloatWindow(file).Show();
                }
                return null;

            case "act:terminal":
                OpenTerminal(files[0]);
                return null;

            case "act:share":
                ShareHelper.Share(files);
                return null;

            case "act:reveal":
                Reveal(files[0]);
                return null;

            default:
                throw new NotSupportedException(option.Id);
        }
    }

    public static void Reveal(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public static void Open(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    static void OpenTerminal(string path)
    {
        string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;

        // Windows Terminal through its app execution alias, PowerShell if it
        // isn't installed.
        string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(wt))
        {
            var info = new ProcessStartInfo(wt) { UseShellExecute = false };
            info.ArgumentList.Add("-d");
            info.ArgumentList.Add(folder);
            Process.Start(info);
            return;
        }
        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            WorkingDirectory = folder,
            ArgumentList = { "-NoExit" },
        });
    }
}

// The system share sheet, the Windows counterpart of AirDrop: nearby
// devices, mail, other apps.
internal static class ShareHelper
{
    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    static readonly Guid DataTransferManagerIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    public static void Share(IReadOnlyList<string> files)
    {
        // The share sheet anchors to a window of ours: a tiny invisible one
        // at the cursor.
        var anchor = new Window
        {
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
        };
        Native.GetCursorPos(out var cursor);
        anchor.Show();
        var hwnd = new WindowInteropHelper(anchor).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, cursor.X, cursor.Y, 1, 1, Native.SWP_NOACTIVATE);
        Native.SetForegroundWindow(hwnd);

        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        Guid iid = DataTransferManagerIid;
        IntPtr result = interop.GetForWindow(hwnd, ref iid);
        var manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(result);

        manager.DataRequested += async (sender, args) =>
        {
            var deferral = args.Request.GetDeferral();
            try
            {
                var items = new List<IStorageItem>();
                foreach (string path in files)
                {
                    items.Add(Directory.Exists(path)
                        ? await StorageFolder.GetFolderFromPathAsync(path)
                        : await StorageFile.GetFileFromPathAsync(path));
                }
                args.Request.Data.Properties.Title = files.Count == 1
                    ? Path.GetFileName(files[0])
                    : $"Файлов: {files.Count}";
                args.Request.Data.SetStorageItems(items);
            }
            catch (Exception ex)
            {
                Log.Write("Share failed: " + ex);
                args.Request.FailWithDisplayText(ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        };

        interop.ShowShareUIForWindow(hwnd);

        // The share sheet runs on its own once open.
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => anchor.Dispatcher.Invoke(anchor.Close));
    }
}

// A file pinned on top of other windows: a picture shows itself, any other
// file shows its icon and can be dragged out again.
internal sealed class FloatWindow : Window
{
    readonly string _path;

    public FloatWindow(string path)
    {
        _path = path;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        Topmost = true;
        Title = Path.GetFileName(path);
        MinWidth = 120;
        MinHeight = 90;

        UIElement body;
        ImageSource? picture = TryLoadPicture(path);
        if (picture != null)
        {
            double scale = Math.Min(1, 360 / Math.Max(picture.Width, picture.Height));
            Width = Math.Max(MinWidth, picture.Width * scale);
            Height = Math.Max(MinHeight, picture.Height * scale);
            body = new Image { Source = picture, Stretch = Stretch.Uniform };
        }
        else
        {
            Width = 240;
            Height = 150;
            body = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Image { Source = ShellIcon(path), Width = 48, Height = 48 },
                    new TextBlock
                    {
                        Text = Path.GetFileName(path.TrimEnd('\\', '/')),
                        FontFamily = Theme.Text,
                        FontSize = 13,
                        Foreground = Theme.Brush(0xFF, 0xF4, 0xF4, 0xF6),
                        TextAlignment = TextAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(10, 8, 10, 0),
                    },
                },
            };
        }

        var dragOut = Glyph("", "Перетащите, чтобы вставить файл куда-нибудь");
        dragOut.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            DragDrop.DoDragDrop(this, new DataObject(DataFormats.FileDrop, new[] { _path }), DragDropEffects.Copy);
        };
        var close = Glyph("", "Закрыть");
        close.MouseLeftButtonUp += (_, _) => Close();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Children = { dragOut, close },
        };

        Content = new Border
        {
            Background = Theme.Brush(0xF0, 0x20, 0x22, 0x27),
            BorderBrush = Theme.Brush(0x50, 0xFF, 0xFF, 0xFF),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4),
            Child = new Grid { Children = { body, toolbar } },
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        MouseDoubleClick += (_, _) => FileActions.Open(_path);
        MouseWheel += (_, e) =>
        {
            double factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
            Width = Math.Max(MinWidth, Width * factor);
            Height = Math.Max(MinHeight, Height * factor);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };

        Native.GetCursorPos(out var cursor);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, cursor.X - 40, cursor.Y - 40, 0, 0, Native.SWP_NOSIZE);
        };
    }

    static TextBlock Glyph(string glyph, string tip) => new()
    {
        Text = glyph,
        FontFamily = Theme.Icons,
        FontSize = 14,
        Padding = new Thickness(6),
        Foreground = Theme.Brush(0xFF, 0xF4, 0xF4, 0xF6),
        Background = Theme.Brush(0xA0, 0x10, 0x10, 0x14),
        Cursor = Cursors.Hand,
        ToolTip = tip,
        Margin = new Thickness(4, 0, 0, 0),
    };

    static ImageSource? TryLoadPicture(string path)
    {
        if (Catalog.KindOf(path) != FileKind.Image)
        {
            return null;
        }
        try
        {
            return ImageOps.Load(path);
        }
        catch
        {
            return null;
        }
    }

    static ImageSource? ShellIcon(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null)
            {
                return null;
            }
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }
}
