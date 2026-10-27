using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace DropWheel;

internal static class ArchiveOps
{
    // Files and folders as they are: a folder becomes a folder inside the
    // archive.
    static IEnumerable<(string fullPath, string entryName)> Entries(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            string trimmed = path.TrimEnd('\\', '/');
            if (Directory.Exists(trimmed))
            {
                string root = Path.GetDirectoryName(trimmed) ?? trimmed;
                foreach (string file in Directory.EnumerateFiles(trimmed, "*", SearchOption.AllDirectories))
                {
                    yield return (file, Path.GetRelativePath(root, file).Replace('\\', '/'));
                }
            }
            else if (File.Exists(trimmed))
            {
                yield return (trimmed, Path.GetFileName(trimmed));
            }
        }
    }

    public static void Zip(IReadOnlyList<string> paths, string zipPath, CancellationToken ct)
    {
        using var stream = File.Create(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (fullPath, entryName) in Entries(paths))
        {
            ct.ThrowIfCancellationRequested();
            archive.CreateEntryFromFile(fullPath, entryName, CompressionLevel.Optimal);
        }
    }

    public static void TarGz(IReadOnlyList<string> paths, string outPath, CancellationToken ct)
    {
        using var stream = File.Create(outPath);
        using var gzip = new GZipStream(stream, CompressionLevel.Optimal);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax);
        foreach (var (fullPath, entryName) in Entries(paths))
        {
            ct.ThrowIfCancellationRequested();
            writer.WriteEntry(fullPath, entryName);
        }
    }

    public static void Extract(string archivePath, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        switch (Catalog.ExtOf(archivePath))
        {
            case "zip":
                ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: false);
                break;

            case "tar":
                TarFile.ExtractToDirectory(archivePath, destination, overwriteFiles: false);
                break;

            case "tar.gz":
                using (var stream = File.OpenRead(archivePath))
                using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                {
                    TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: false);
                }
                break;

            default:
                // 7z, RAR and the rest: the libarchive-based tar.exe that ships
                // with Windows reads them.
                ExtractWithSystemTar(archivePath, destination, ct);
                break;
        }
    }

    static void ExtractWithSystemTar(string archivePath, string destination, CancellationToken ct)
    {
        string tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        var info = new ProcessStartInfo(tar)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("-xf");
        info.ArgumentList.Add(archivePath);
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(destination);

        using var process = Process.Start(info)!;
        using var registration = ct.Register(() =>
        {
            try
            {
                process.Kill();
            }
            catch
            {
            }
        });
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Не удалось распаковать: " + error.Trim());
        }
    }

    // ZIP <-> TAR.GZ: unpack into a temporary folder and pack its contents.
    public static void Repack(string archivePath, string outPath, string format, CancellationToken ct)
    {
        string temp = Path.Combine(Path.GetTempPath(), "DropWheel-" + Guid.NewGuid().ToString("N"));
        try
        {
            Extract(archivePath, temp, ct);
            var items = new List<string>();
            items.AddRange(Directory.GetDirectories(temp));
            items.AddRange(Directory.GetFiles(temp));
            if (format == "zip")
            {
                Zip(items, outPath, ct);
            }
            else
            {
                TarGz(items, outPath, ct);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(temp, true);
            }
            catch
            {
            }
        }
    }
}
