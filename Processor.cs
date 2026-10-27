using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace DropWheel;

internal sealed class JobResult
{
    public List<string> Outputs { get; } = new();
    public List<string> Errors { get; } = new();
    public int Skipped { get; set; }
}

// Runs a conversion or tool over the dropped files. Results go next to the
// originals, which are never touched.
internal static class Processor
{
    sealed class DecodeException : Exception
    {
        public DecodeException(Exception inner) : base(inner.Message, inner)
        {
        }
    }

    public static async Task<JobResult> RunAsync(
        WheelOption option,
        IReadOnlyList<string> files,
        Action<double, string> progress,
        CancellationToken ct)
    {
        var result = new JobResult();

        if (option.Id == "tool:zip")
        {
            progress(0, "архив");
            string output = ZipOutputPath(files);
            await Task.Run(() => ArchiveOps.Zip(files, output, ct), ct);
            result.Outputs.Add(output);
            return result;
        }

        var targets = files.Where(f => option.Kind == null || Catalog.KindOf(f) == option.Kind).ToList();
        result.Skipped = files.Count - targets.Count;
        WritableDirs.Clear();
        ReservedPaths.Clear();

        // Pictures and sound go several at a time: one file keeps only one
        // core busy. Video already uses every core in FFmpeg, and Office
        // documents go through one Word/Excel/PowerPoint, so those stay in
        // a row.
        int parallel = option.Kind is FileKind.Image or FileKind.Audio && targets.Count > 1
            ? Math.Clamp(Environment.ProcessorCount / 2, 2, 6)
            : 1;
        var outcomes = new (string? output, string? error)[targets.Count];
        int done = 0;
        int next = -1;

        async Task Worker()
        {
            while (true)
            {
                int i = Interlocked.Increment(ref next);
                if (i >= targets.Count)
                {
                    return;
                }
                ct.ThrowIfCancellationRequested();
                string file = targets[i];
                string name = Path.GetFileName(file);
                if (parallel == 1)
                {
                    progress((double)i / targets.Count, name);
                }
                outcomes[i] = await RunSafelyAsync(option, file, name,
                    p =>
                    {
                        if (parallel == 1)
                        {
                            progress((i + p) / targets.Count, name);
                        }
                    }, ct);
                int finished = Interlocked.Increment(ref done);
                if (parallel > 1)
                {
                    progress((double)finished / targets.Count, name);
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, parallel).Select(_ => Task.Run(Worker, ct)));

        foreach (var (output, error) in outcomes)
        {
            if (output != null)
            {
                result.Outputs.Add(output);
            }
            if (error != null)
            {
                result.Errors.Add(error);
            }
        }
        ReservedPaths.Clear();
        return result;
    }

    // One file: its result or a message for the user, never an exception
    // other than cancellation.
    static async Task<(string? output, string? error)> RunSafelyAsync(
        WheelOption option, string file, string name, Action<double> progress, CancellationToken ct)
    {
        // "New > Bitmap image" and the like create files with nothing in
        // them; say so instead of a decoder error.
        if (File.Exists(file) && new FileInfo(file).Length == 0)
        {
            return (null, $"{name}: файл пустой (0 байт), в нём нечего обрабатывать");
        }
        try
        {
            string output = await RunOneAsync(option, file, progress, ct);
            if (IsCompression(option) && !IsSmaller(output, file))
            {
                TryDelete(output);
                return (null, $"{name}: уже сжат лучше, результат вышел бы не меньше");
            }
            return (output, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write($"{option.Id} failed for {file}: {ex}");
            return (null, $"{name}: {ex.Message}");
        }
    }

    static bool IsCompression(WheelOption option) =>
        option.Id is "tool:compress" or "tool:vcompress" or "tool:acompress";

    static bool IsSmaller(string output, string source)
    {
        try
        {
            return new FileInfo(output).Length < new FileInfo(source).Length;
        }
        catch
        {
            return true;
        }
    }

    static Task<string> RunOneAsync(WheelOption option, string file, Action<double> progress, CancellationToken ct)
    {
        string target = option.Target;
        return option.Kind switch
        {
            FileKind.Image => ImageAsync(option.Id, file, ct),
            FileKind.Video => VideoAsync(option.Id, target, file, progress, ct),
            FileKind.Audio => AudioAsync(option.Id, target, file, progress, ct),
            FileKind.Archive => ArchiveAsync(option.Id, target, file, ct),
            FileKind.Document or FileKind.Spreadsheet or FileKind.Presentation =>
                OfficeAsync(option.Kind.Value, target, file, ct),
            _ => throw new NotSupportedException(option.Id),
        };
    }

    // ------------------------------------------------------------ documents

    static async Task<string> OfficeAsync(FileKind kind, string target, string file, CancellationToken ct)
    {
        string output = OutputPath(file, target, null);
        try
        {
            await OfficeOps.ConvertAsync(kind, file, target, output, ct);
        }
        catch
        {
            TryDelete(output);
            throw;
        }
        if (!File.Exists(output))
        {
            throw new InvalidOperationException("Office не создал файл");
        }
        return output;
    }

    // ---------------------------------------------------------------- images

    static readonly HashSet<string> WicFormats = new() { "jpg", "png", "gif", "bmp", "tiff", "ico" };

    static Task<string> ImageAsync(string id, string file, CancellationToken ct)
    {
        int quality = Settings.Current.JpegQuality;
        return id switch
        {
            "tool:compress" => ImageJobAsync(file, null, ChooseCompressedFormat, "сжато", null, 72, ct),
            "tool:half" => ImageJobAsync(file, SameImageFormat(file), null, "½", i => ImageOps.Scale(i, 0.5), quality, ct),
            "tool:rotate" => ImageJobAsync(file, SameImageFormat(file), null, "повёрнуто", ImageOps.Rotate90, quality, ct),
            "tool:flip" => ImageJobAsync(file, SameImageFormat(file), null, "отражено", ImageOps.FlipHorizontal, quality, ct),
            "tool:gray" => ImageJobAsync(file, SameImageFormat(file), null, "ч-б", ImageOps.Grayscale, quality, ct),
            "tool:strip" => ImageJobAsync(file, SameImageFormat(file), null, "без EXIF", null, 95, ct),
            _ when id.StartsWith("conv:", StringComparison.Ordinal) =>
                ImageJobAsync(file, id[5..], null, null, null, quality, ct),
            _ => throw new NotSupportedException(id),
        };
    }

    static string SameImageFormat(string file)
    {
        string ext = Catalog.ExtOf(file);
        if (WicFormats.Contains(ext))
        {
            return ext;
        }
        if ((ext == "webp" || ext == "avif") && Ffmpeg.IsAvailable)
        {
            return ext;
        }
        return ext == "heic" ? "jpg" : "png";
    }

    // Photos become JPG; pictures with transparency keep it in WebP.
    static string ChooseCompressedFormat(BitmapSource image)
    {
        if (ImageOps.HasTransparency(image))
        {
            return Ffmpeg.IsAvailable ? "webp" : "png";
        }
        return "jpg";
    }

    static async Task<string> ImageJobAsync(
        string file,
        string? format,
        Func<BitmapSource, string>? chooseFormat,
        string? suffix,
        Func<BitmapSource, BitmapSource>? transform,
        int quality,
        CancellationToken ct)
    {
        var temps = new List<string>();
        try
        {
            // Decodes, transforms and, for formats WIC can write, saves the
            // result; other formats come back as a PNG for FFmpeg to encode.
            Task<(string output, string format, string? png)> Stage(string input) => StaRunner.Run(() =>
            {
                BitmapSource image;
                try
                {
                    image = ImageOps.Load(input);
                }
                catch (Exception ex)
                {
                    throw new DecodeException(ex);
                }
                if (transform != null)
                {
                    image = transform(image);
                }
                string chosen = format ?? chooseFormat!(image);
                string output = OutputPath(file, chosen, suffix);
                if (WicFormats.Contains(chosen))
                {
                    ImageOps.Save(image, output, chosen, quality);
                    return (output, chosen, (string?)null);
                }
                string png = TempFile("png");
                lock (temps)
                {
                    temps.Add(png);
                }
                ImageOps.Save(image, png, "png", quality);
                return (output, chosen, png);
            });

            (string output, string format, string? png) staged;
            try
            {
                staged = await Stage(file);
            }
            catch (DecodeException ex)
            {
                if (!Ffmpeg.IsAvailable)
                {
                    throw new InvalidOperationException(
                        "Windows не читает этот формат (" + ex.Message + "). Скачайте FFmpeg из меню в трее " +
                        "или установите расширение для HEIC/WebP/AVIF из Microsoft Store.");
                }
                string decoded = TempFile("png");
                temps.Add(decoded);
                await Ffmpeg.RunAsync(new[] { "-i", file, "-frames:v", "1", decoded }, 0, null, ct);
                staged = await Stage(decoded);
            }

            if (staged.png != null)
            {
                await EncodeImageWithFfmpegAsync(staged.png, staged.output, staged.format, quality, ct);
            }
            return staged.output;
        }
        finally
        {
            foreach (string temp in temps)
            {
                TryDelete(temp);
            }
        }
    }

    static async Task EncodeImageWithFfmpegAsync(string png, string output, string format, int quality, CancellationToken ct)
    {
        var args = new List<string> { "-i", png };
        if (format == "webp")
        {
            args.AddRange(new[] { "-c:v", "libwebp", "-quality", Math.Min(quality, 90).ToString(CultureInfo.InvariantCulture) });
        }
        else if (format == "avif")
        {
            if (await Ffmpeg.HasEncoderAsync("libaom-av1"))
            {
                args.AddRange(new[] { "-c:v", "libaom-av1", "-still-picture", "1", "-crf", "30", "-cpu-used", "6", "-pix_fmt", "yuv420p" });
            }
            else if (await Ffmpeg.HasEncoderAsync("libsvtav1"))
            {
                args.AddRange(new[] { "-c:v", "libsvtav1", "-crf", "30", "-pix_fmt", "yuv420p" });
            }
            else
            {
                throw new InvalidOperationException("В этой сборке FFmpeg нет кодировщика AV1");
            }
        }
        args.AddRange(new[] { "-frames:v", "1", output });
        await Ffmpeg.RunAsync(args, 0, null, ct);
    }

    // ----------------------------------------------------------------- video

    static List<string> H264Video(string crf) =>
        new() { "-c:v", "libx264", "-preset", "veryfast", "-crf", crf, "-pix_fmt", "yuv420p" };

    static List<string> MainStreams(bool audio)
    {
        var args = new List<string> { "-map", "0:v:0" };
        if (audio)
        {
            args.AddRange(new[] { "-map", "0:a:0" });
        }
        return args;
    }

    static async Task<string> VideoAsync(string id, string target, string file, Action<double> progress, CancellationToken ct)
    {
        MediaInfo info = await Ffmpeg.ProbeAsync(file, ct);
        double duration = info.Duration;
        bool audio = info.HasAudio;
        var args = new List<string> { "-i", file };
        string output;

        switch (id)
        {
            case "conv:mp4":
            case "conv:mov":
                output = OutputPath(file, target, null);
                args.AddRange(MainStreams(audio));
                args.AddRange(H264Video("22"));
                if (audio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
                args.AddRange(new[] { "-movflags", "+faststart" });
                break;

            case "conv:mkv":
                // MKV takes almost any codec, so the streams are copied as is.
                output = OutputPath(file, "mkv", null);
                try
                {
                    await Ffmpeg.RunAsync(new[] { "-i", file, "-map", "0", "-c", "copy", output }, duration, progress, ct);
                    return output;
                }
                catch (FfmpegException)
                {
                    TryDelete(output);
                    args.AddRange(MainStreams(audio));
                    args.AddRange(H264Video("22"));
                    if (audio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
                }
                break;

            case "conv:webm":
                output = OutputPath(file, "webm", null);
                args.AddRange(MainStreams(audio));
                args.AddRange(new[]
                {
                    "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-row-mt", "1",
                    "-b:v", "0", "-crf", "33",
                });
                if (audio) args.AddRange(new[] { "-c:a", "libopus", "-b:a", "128k" });
                break;

            case "conv:avi":
                output = OutputPath(file, "avi", null);
                args.AddRange(MainStreams(audio));
                args.AddRange(new[] { "-c:v", "mpeg4", "-q:v", "4" });
                if (audio) args.AddRange(new[] { "-c:a", "libmp3lame", "-q:a", "3" });
                break;

            case "conv:gif":
                output = OutputPath(file, "gif", null);
                args.AddRange(new[]
                {
                    "-vf",
                    "fps=15,scale='min(640,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];" +
                    "[b][p]paletteuse=dither=bayer:bayer_scale=4",
                    "-loop", "0",
                });
                break;

            case "conv:mp3":
            case "conv:m4a":
            case "conv:wav":
                if (!audio) throw new InvalidOperationException("в видео нет звуковой дорожки");
                output = OutputPath(file, target, null);
                args.AddRange(new[] { "-vn", "-map", "0:a:0" });
                args.AddRange(AudioCodec(target));
                break;

            case "tool:vcompress":
                output = OutputPath(file, "mp4", "сжато");
                args.AddRange(MainStreams(audio));
                args.AddRange(new[] { "-vf", "scale='min(1920,iw)':-2" });
                args.AddRange(H264Video("28"));
                if (audio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "128k" });
                args.AddRange(new[] { "-movflags", "+faststart" });
                break;

            case "tool:720p":
                output = OutputPath(file, "mp4", "720p");
                args.AddRange(MainStreams(audio));
                args.AddRange(new[] { "-vf", "scale=-2:'min(720,ih)'" });
                args.AddRange(H264Video("23"));
                if (audio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
                args.AddRange(new[] { "-movflags", "+faststart" });
                break;

            case "tool:mute":
                output = OutputPath(file, Catalog.ExtOf(file), "без звука");
                args.AddRange(new[] { "-map", "0:v", "-c", "copy", "-an" });
                break;

            case "tool:vrotate":
                output = OutputPath(file, "mp4", "повёрнуто");
                args.AddRange(MainStreams(audio));
                args.AddRange(new[] { "-vf", "transpose=1" });
                args.AddRange(H264Video("22"));
                if (audio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
                break;

            case "tool:frame":
            {
                output = OutputPath(file, "png", "кадр");
                double at = duration > 2 ? 1 : duration / 2;
                args = new List<string>
                {
                    "-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-i", file, "-frames:v", "1",
                };
                duration = 0;
                break;
            }

            case "tool:speed2":
                output = OutputPath(file, "mp4", "×2");
                if (audio)
                {
                    args.AddRange(new[]
                    {
                        "-filter_complex", "[0:v:0]setpts=0.5*PTS[v];[0:a:0]atempo=2.0[a]",
                        "-map", "[v]", "-map", "[a]", "-c:a", "aac", "-b:a", "160k",
                    });
                }
                else
                {
                    args.AddRange(new[] { "-map", "0:v:0", "-vf", "setpts=0.5*PTS" });
                }
                args.AddRange(H264Video("22"));
                duration /= 2;
                break;

            default:
                throw new NotSupportedException(id);
        }

        args.Add(output);
        await RunOrCleanUpAsync(args, duration, output, progress, ct);
        return output;
    }

    // ----------------------------------------------------------------- audio

    static string[] AudioCodec(string ext) => ext switch
    {
        "mp3" => new[] { "-c:a", "libmp3lame", "-q:a", "2" },
        "m4a" => new[] { "-c:a", "aac", "-b:a", "192k" },
        "wav" => new[] { "-c:a", "pcm_s16le" },
        "flac" => new[] { "-c:a", "flac" },
        "ogg" => new[] { "-c:a", "libvorbis", "-q:a", "5" },
        "opus" => new[] { "-c:a", "libopus", "-b:a", "128k" },
        "aiff" => new[] { "-c:a", "pcm_s16be" },
        _ => Array.Empty<string>(),
    };

    static async Task<string> AudioAsync(string id, string target, string file, Action<double> progress, CancellationToken ct)
    {
        MediaInfo info = await Ffmpeg.ProbeAsync(file, ct);
        if (!info.HasAudio)
        {
            throw new InvalidOperationException("в файле нет звука");
        }
        string ext = Catalog.ExtOf(file);
        var args = new List<string> { "-i", file, "-vn", "-map", "0:a:0" };
        string output;

        switch (id)
        {
            case "tool:acompress":
                output = OutputPath(file, "mp3", "сжато");
                args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", "128k" });
                break;

            case "tool:normalize":
                output = OutputPath(file, ext, "громкость");
                args.AddRange(new[] { "-af", "loudnorm=I=-16:TP=-1.5:LRA=11" });
                args.AddRange(AudioCodec(ext));
                // loudnorm works at 192 kHz, the file keeps its own rate.
                args.AddRange(new[]
                {
                    "-ar", (info.SampleRate > 0 ? info.SampleRate : 48000).ToString(CultureInfo.InvariantCulture),
                });
                break;

            case "tool:mono":
                output = OutputPath(file, ext, "моно");
                args.AddRange(new[] { "-ac", "1" });
                args.AddRange(AudioCodec(ext));
                break;

            case "tool:trimsilence":
                output = OutputPath(file, ext, "без тишины");
                const string trim = "silenceremove=start_periods=1:start_silence=0.1:start_threshold=-50dB";
                args.AddRange(new[] { "-af", $"{trim},areverse,{trim},areverse" });
                args.AddRange(AudioCodec(ext));
                break;

            default:
                output = OutputPath(file, target, null);
                args.AddRange(AudioCodec(target));
                break;
        }

        args.Add(output);
        await RunOrCleanUpAsync(args, info.Duration, output, progress, ct);
        return output;
    }

    static async Task RunOrCleanUpAsync(List<string> args, double duration, string output, Action<double> progress, CancellationToken ct)
    {
        try
        {
            await Ffmpeg.RunAsync(args, duration, progress, ct);
        }
        catch
        {
            TryDelete(output);
            throw;
        }
    }

    // -------------------------------------------------------------- archives

    static async Task<string> ArchiveAsync(string id, string target, string file, CancellationToken ct)
    {
        if (id == "tool:extract")
        {
            string folder = UniqueFolder(OutputDirectory(file), Catalog.BaseNameOf(file));
            await Task.Run(() => ArchiveOps.Extract(file, folder, ct), ct);
            return folder;
        }

        string output = OutputPath(file, target, null);
        await Task.Run(() => ArchiveOps.Repack(file, output, target, ct), ct);
        return output;
    }

    static string ZipOutputPath(IReadOnlyList<string> files)
    {
        string first = files[0].TrimEnd('\\', '/');
        string name = files.Count == 1
            ? Catalog.BaseNameOf(first)
            : Path.GetFileName(Path.GetDirectoryName(first) ?? "") is { Length: > 0 } parent ? parent : "Архив";
        return UniquePath(OutputDirectory(first), name, "zip");
    }

    // ----------------------------------------------------------------- paths

    // Per job: whether a folder takes new files (checked once, not for each
    // file) and which output names are handed out.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> WritableDirs = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> ReservedPaths = new(StringComparer.OrdinalIgnoreCase);

    static string OutputDirectory(string source)
    {
        string dir = Path.GetDirectoryName(source.TrimEnd('\\', '/')) ?? "";
        return dir.Length > 0 && WritableDirs.GetOrAdd(dir, CanWrite)
            ? dir
            : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".dropwheel-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static string OutputPath(string source, string ext, string? suffix)
    {
        string name = Catalog.BaseNameOf(source) + (suffix == null ? "" : $" ({suffix})");
        return UniquePath(OutputDirectory(source), name, ext);
    }

    static string UniquePath(string dir, string name, string ext)
    {
        lock (ReservedPaths)
        {
            string path = Path.Combine(dir, $"{name}.{ext}");
            for (int i = 2; Taken(path); i++)
            {
                path = Path.Combine(dir, $"{name} ({i}).{ext}");
            }
            ReservedPaths.Add(path);
            return path;
        }
    }

    static string UniqueFolder(string dir, string name)
    {
        lock (ReservedPaths)
        {
            string path = Path.Combine(dir, name);
            for (int i = 2; Taken(path); i++)
            {
                path = Path.Combine(dir, $"{name} ({i})");
            }
            ReservedPaths.Add(path);
            return path;
        }
    }

    // Several files are processed at once: a name counts as taken from the
    // moment it's handed out, before the file exists.
    static bool Taken(string path) => ReservedPaths.Contains(path) || File.Exists(path) || Directory.Exists(path);

    static string TempFile(string ext) =>
        Path.Combine(Path.GetTempPath(), $"DropWheel-{Guid.NewGuid():N}.{ext}");

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
