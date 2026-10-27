using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DropWheel;

internal sealed record MediaInfo(double Duration, bool HasVideo, bool HasAudio, int Width, int Height, int SampleRate);

internal sealed class FfmpegException : Exception
{
    public FfmpegException(string message) : base(message)
    {
    }
}

internal static class Ffmpeg
{
    // The "essentials" build from gyan.dev, the Windows builds linked from
    // ffmpeg.org: x264, VP9, Opus, Vorbis, LAME, WebP, AV1 included. The same
    // builds are published on the author's GitHub, which is usually much
    // faster; gyan.dev itself is the fallback and the source of the checksum.
    const string GitHubLatestRelease = "https://api.github.com/repos/GyanD/codexffmpeg/releases/latest";
    const string GyanZip = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    const string GyanSha256 = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256";
    const string GyanVersion = "https://www.gyan.dev/ffmpeg/builds/release-version";

    static readonly string DownloadedPath = Path.Combine(Settings.Folder, "ffmpeg", "ffmpeg.exe");

    static string? _cachedPath;
    static HashSet<string>? _encoders;

    public static string? Locate()
    {
        if (_cachedPath != null && File.Exists(_cachedPath))
        {
            return _cachedPath;
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(Settings.Current.FfmpegPath))
        {
            candidates.Add(Settings.Current.FfmpegPath);
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));
        candidates.Add(DownloadedPath);
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "ffmpeg.exe"));
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (dir.Length > 0)
            {
                candidates.Add(Path.Combine(dir.Trim('"'), "ffmpeg.exe"));
            }
        }

        _cachedPath = candidates.FirstOrDefault(File.Exists);
        return _cachedPath;
    }

    // Asked for every sector of the wheel: looked up on disk at most every
    // 10 seconds, not each time (PATH may hold slow network folders).
    static long? _checkedAt;
    static bool _available;

    public static bool IsAvailable
    {
        get
        {
            long now = Environment.TickCount64;
            if (_checkedAt is not long at || now - at > 10_000)
            {
                _available = Locate() != null;
                _checkedAt = now;
            }
            return _available;
        }
    }

    static string Require() =>
        Locate() ?? throw new FfmpegException("FFmpeg не найден. Скачайте его из меню в трее.");

    static ProcessStartInfo StartInfo(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(Require())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        return info;
    }

    public static async Task<MediaInfo> ProbeAsync(string input, CancellationToken ct)
    {
        using var process = Process.Start(StartInfo(new[] { "-hide_banner", "-nostdin", "-i", input }))!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await stdout;
        await process.WaitForExitAsync(ct);

        double duration = 0;
        var match = Regex.Match(stderr, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
        if (match.Success)
        {
            duration = int.Parse(match.Groups[1].Value) * 3600 +
                       int.Parse(match.Groups[2].Value) * 60 +
                       double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        bool hasVideo = false;
        int width = 0, height = 0;
        foreach (Match stream in Regex.Matches(stderr, @"Stream #\d+:\d+.*?: Video: (.*)"))
        {
            // Cover art in audio files shows up as a still video stream.
            if (stream.Value.Contains("attached pic", StringComparison.Ordinal))
            {
                continue;
            }
            hasVideo = true;
            var size = Regex.Match(stream.Groups[1].Value, @"\b(\d{2,5})x(\d{2,5})\b");
            if (size.Success)
            {
                width = int.Parse(size.Groups[1].Value);
                height = int.Parse(size.Groups[2].Value);
            }
            break;
        }

        var audio = Regex.Match(stderr, @"Stream #\d+:\d+.*?: Audio: .*?(\d+) Hz");
        bool hasAudio = audio.Success || stderr.Contains(": Audio:", StringComparison.Ordinal);
        int sampleRate = audio.Success ? int.Parse(audio.Groups[1].Value) : 0;

        if (!hasVideo && !hasAudio)
        {
            throw new FfmpegException("FFmpeg не смог прочитать файл: " + LastLines(stderr));
        }
        return new MediaInfo(duration, hasVideo, hasAudio, width, height, sampleRate);
    }

    // Runs one conversion. durationSec is the expected output duration, for
    // the progress fraction.
    public static async Task RunAsync(IEnumerable<string> args, double durationSec, Action<double>? progress, CancellationToken ct)
    {
        var fullArgs = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats",
        };
        fullArgs.AddRange(args);

        using var process = Process.Start(StartInfo(fullArgs))!;
        using var registration = ct.Register(() =>
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
            }
        });

        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync()) != null)
        {
            if (progress == null || durationSec <= 0)
            {
                continue;
            }
            // out_time_us and the oddly named out_time_ms are both microseconds.
            if ((line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
                 line.StartsWith("out_time_ms=", StringComparison.Ordinal)) &&
                long.TryParse(line[(line.IndexOf('=') + 1)..], out long us))
            {
                progress(Math.Clamp(us / 1_000_000.0 / durationSec, 0, 1));
            }
        }

        string stderr = await stderrTask;
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            throw new FfmpegException(LastLines(stderr));
        }
        progress?.Invoke(1);
    }

    public static async Task<bool> HasEncoderAsync(string name)
    {
        if (_encoders == null)
        {
            var encoders = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                using var process = Process.Start(StartInfo(new[] { "-hide_banner", "-encoders" }))!;
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                foreach (Match m in Regex.Matches(output, @"^\s*[VAS][\w.]{5}\s+(\S+)", RegexOptions.Multiline))
                {
                    encoders.Add(m.Groups[1].Value);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Listing encoders failed: " + ex.Message);
            }
            _encoders = encoders;
        }
        return _encoders.Contains(name);
    }

    static string LastLines(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "неизвестная ошибка FFmpeg" : string.Join(" ", lines.TakeLast(2));
    }

    public static async Task DownloadAsync(Action<double> progress, CancellationToken ct)
    {
        string folder = Path.GetDirectoryName(DownloadedPath)!;
        Directory.CreateDirectory(folder);
        string zipPath = Path.Combine(folder, "ffmpeg-download.zip");

        using var http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DropWheel/0.1");

        string? version = null;
        try
        {
            (string url, version) = await LatestGitHubBuildAsync(http, ct);
            await DownloadFileAsync(http, url, zipPath, progress, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Write("GitHub download failed, trying gyan.dev: " + ex.Message);
            version = null;
            await DownloadFileAsync(http, GyanZip, zipPath, progress, ct);
        }

        await VerifyAsync(http, zipPath, version, ct);

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                throw new FfmpegException("В архиве не нашлось ffmpeg.exe");
            }
            entry.ExtractToFile(DownloadedPath, true);
        }
        File.Delete(zipPath);

        _cachedPath = null;
        _checkedAt = null;
        _encoders = null;
    }

    static async Task<(string url, string version)> LatestGitHubBuildAsync(HttpClient http, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        string json = await http.GetStringAsync(GitHubLatestRelease, timeout.Token);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        string version = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith("essentials_build.zip", StringComparison.OrdinalIgnoreCase))
            {
                return (asset.GetProperty("browser_download_url").GetString()!, version);
            }
        }
        throw new FfmpegException("в релизе нет сборки essentials");
    }

    static async Task DownloadFileAsync(HttpClient http, string url, string path, Action<double> progress,
        CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(path);
        var buffer = new byte[1 << 16];
        long done = 0;
        int read;
        while (true)
        {
            // A stalled connection ends the attempt instead of hanging.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(TimeSpan.FromSeconds(60));
            read = await source.ReadAsync(buffer, stall.Token);
            if (read == 0)
            {
                break;
            }
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
            {
                progress(done / (double)total);
            }
        }
        if (total > 0 && done != total)
        {
            throw new FfmpegException("загрузка оборвалась");
        }
    }

    // Checked against the SHA-256 that gyan.dev publishes for its current
    // release, when the downloaded build is that release.
    static async Task VerifyAsync(HttpClient http, string zipPath, string? version, CancellationToken ct)
    {
        string expected;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            if (version != null)
            {
                string current = (await http.GetStringAsync(GyanVersion, timeout.Token)).Trim();
                if (current != version)
                {
                    Log.Write($"FFmpeg {version} downloaded, gyan.dev lists {current}: no checksum to compare");
                    return;
                }
            }
            expected = (await http.GetStringAsync(GyanSha256, timeout.Token)).Trim().Split(' ')[0].ToLowerInvariant();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Write("FFmpeg checksum unavailable: " + ex.Message);
            return;
        }

        string actual;
        await using (var stream = File.OpenRead(zipPath))
        {
            actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct))
                .ToLowerInvariant();
        }
        if (actual != expected)
        {
            File.Delete(zipPath);
            throw new FfmpegException("контрольная сумма FFmpeg не совпала, файл удалён");
        }
    }
}
