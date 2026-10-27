using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DropWheel;

// WPF imaging objects need an STA thread, conversions get one each.
internal static class StaRunner
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    public static Task Run(Action work) => Run(() =>
    {
        work();
        return true;
    });
}

// Image work through WIC, the codecs built into Windows. HEIC, WebP and AVIF
// decode when their Microsoft Store extensions are installed; otherwise the
// caller falls back to FFmpeg.
internal static class ImageOps
{
    public static BitmapSource Load(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapFrame frame = decoder.Frames[0];
        BitmapSource image = ApplyOrientation(frame, ReadOrientation(frame.Metadata as BitmapMetadata));
        image.Freeze();
        return image;
    }

    static ushort ReadOrientation(BitmapMetadata? metadata)
    {
        if (metadata == null)
        {
            return 1;
        }
        foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                if (metadata.GetQuery(query) is ushort value)
                {
                    return value;
                }
            }
            catch
            {
            }
        }
        return 1;
    }

    // EXIF orientation: phones store photos sideways and only tag the turn.
    // The pixels are rotated for real, since the tag is dropped on saving.
    static BitmapSource ApplyOrientation(BitmapSource image, ushort orientation) => orientation switch
    {
        2 => Transform(image, new ScaleTransform(-1, 1)),
        3 => Transform(image, new RotateTransform(180)),
        4 => Transform(image, new ScaleTransform(1, -1)),
        5 => Transform(Transform(image, new RotateTransform(90)), new ScaleTransform(-1, 1)),
        6 => Transform(image, new RotateTransform(90)),
        7 => Transform(Transform(image, new RotateTransform(270)), new ScaleTransform(-1, 1)),
        8 => Transform(image, new RotateTransform(270)),
        _ => image,
    };

    static BitmapSource Transform(BitmapSource image, Transform transform)
    {
        var result = new TransformedBitmap(image, transform);
        result.Freeze();
        return result;
    }

    public static BitmapSource Rotate90(BitmapSource image) => Transform(image, new RotateTransform(90));

    public static BitmapSource FlipHorizontal(BitmapSource image) => Transform(image, new ScaleTransform(-1, 1));

    public static BitmapSource Scale(BitmapSource image, double factor)
    {
        int width = Math.Max(1, (int)Math.Round(image.PixelWidth * factor));
        int height = Math.Max(1, (int)Math.Round(image.PixelHeight * factor));
        return Render(width, height, null, dc => dc.DrawImage(image, new Rect(0, 0, width, height)));
    }

    public static BitmapSource Grayscale(BitmapSource image)
    {
        var source = ToBgra(image);
        int stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte gray = (byte)Math.Round(pixels[i] * 0.114 + pixels[i + 1] * 0.587 + pixels[i + 2] * 0.299);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = gray;
        }
        var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    // True only when some pixel really is see-through: plenty of PNGs carry
    // an alpha channel that is opaque everywhere.
    public static bool HasTransparency(BitmapSource image)
    {
        PixelFormat format = image.Format;
        bool alphaFormat = format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 ||
                           format == PixelFormats.Rgba64 || format == PixelFormats.Prgba64 ||
                           format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float ||
                           format == PixelFormats.Indexed8 || format == PixelFormats.Indexed4 ||
                           format == PixelFormats.Indexed2 || format == PixelFormats.Indexed1;
        if (!alphaFormat)
        {
            return false;
        }
        var bgra = ToBgra(image);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255)
            {
                return true;
            }
        }
        return false;
    }

    static BitmapSource ToBgra(BitmapSource image)
    {
        if (image.Format == PixelFormats.Bgra32)
        {
            return image;
        }
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    static BitmapSource Render(int width, int height, Brush? background, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            if (background != null)
            {
                dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            }
            draw(dc);
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var result = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        result.Freeze();
        return result;
    }

    // Formats without transparency get it flattened onto white, not black.
    static BitmapSource Opaque(BitmapSource image)
    {
        BitmapSource source = image;
        if (HasTransparency(image))
        {
            source = Render(image.PixelWidth, image.PixelHeight, Brushes.White,
                dc => dc.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight)));
        }
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        converted.Freeze();
        return converted;
    }

    public static void Save(BitmapSource image, string path, string format, int jpegQuality)
    {
        if (format == "ico")
        {
            SaveIco(image, path);
            return;
        }

        BitmapEncoder encoder;
        BitmapSource frame = image;
        switch (format)
        {
            case "jpg":
                encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) };
                frame = Opaque(image);
                break;
            case "bmp":
                encoder = new BmpBitmapEncoder();
                frame = Opaque(image);
                break;
            case "png":
                encoder = new PngBitmapEncoder();
                break;
            case "gif":
                encoder = new GifBitmapEncoder();
                break;
            case "tiff":
                encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
                break;
            default:
                throw new NotSupportedException("WIC не умеет сохранять " + format);
        }

        // A fresh frame without the source metadata: nothing like GPS
        // coordinates is carried over.
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // An icon with PNG frames for every common size, the picture centered on
    // a transparent square.
    static void SaveIco(BitmapSource image, string path)
    {
        int[] sizes = { 256, 128, 64, 48, 32, 16 };
        var frames = new List<byte[]>();
        foreach (int size in sizes)
        {
            double scale = Math.Min((double)size / image.PixelWidth, (double)size / image.PixelHeight);
            double width = image.PixelWidth * scale;
            double height = image.PixelHeight * scale;
            var square = Render(size, size, null, dc =>
                dc.DrawImage(image, new Rect((size - width) / 2, (size - height) / 2, width, height)));

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(square));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            frames.Add(buffer.ToArray());
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }
        foreach (byte[] frame in frames)
        {
            writer.Write(frame);
        }
    }
}
