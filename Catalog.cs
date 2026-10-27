using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DropWheel;

internal enum WheelMode
{
    Convert,
    Tools,
    Actions,
}

internal enum FileKind
{
    Image,
    Video,
    Audio,
    Archive,
    Document,
    Spreadsheet,
    Presentation,
    Folder,
    Other,
}

// One sector of the wheel. Kind is the file kind the option applies to; null
// means any file (archiving, actions).
internal sealed record WheelOption(
    string Id,
    string Label,
    string Sub,
    string Glyph,
    FileKind? Kind,
    bool NeedsFfmpeg)
{
    public bool IsAction => Id.StartsWith("act:", StringComparison.Ordinal);
    public bool IsConversion => Id.StartsWith("conv:", StringComparison.Ordinal);
    public string Target => Id[(Id.IndexOf(':') + 1)..];
}

internal static class Catalog
{
    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "png", "webp", "bmp", "gif", "tiff", "heic", "avif", "ico",
    };

    static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "flv", "mpg", "mpeg", "ts", "mts", "m2ts", "3gp",
    };

    static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "m4a", "aac", "wav", "flac", "ogg", "oga", "opus", "wma", "aiff", "amr",
    };

    static readonly HashSet<string> ArchiveExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "tar", "tar.gz", "7z", "rar",
    };

    static readonly HashSet<string> DocumentExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "rtf", "doc", "docx", "docm", "odt", "html", "wps",
    };

    static readonly HashSet<string> SpreadsheetExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "xlsx", "xls", "xlsm", "xlsb", "csv", "ods",
    };

    static readonly HashSet<string> PresentationExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "pptx", "ppt", "pptm", "odp", "pps", "ppsx",
    };

    // The extension without the dot, with spelling variants folded together.
    public static string ExtOf(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tgz", StringComparison.Ordinal))
        {
            return "tar.gz";
        }
        string ext = Path.GetExtension(name).TrimStart('.');
        return ext switch
        {
            "jpeg" or "jpe" or "jfif" => "jpg",
            "tif" => "tiff",
            "heif" => "heic",
            "aif" => "aiff",
            "htm" => "html",
            "markdown" => "md",
            _ => ext,
        };
    }

    // The file name without the extension, "photo.tar.gz" -> "photo".
    public static string BaseNameOf(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        string ext = ExtOf(path);
        if (Directory.Exists(path) || ext.Length == 0)
        {
            return name;
        }
        if (ext == "tar.gz")
        {
            return name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name[..^7];
        }
        return Path.GetFileNameWithoutExtension(name);
    }

    public static FileKind KindOf(string path)
    {
        if (Directory.Exists(path))
        {
            return FileKind.Folder;
        }
        string ext = ExtOf(path);
        if (ImageExt.Contains(ext)) return FileKind.Image;
        if (VideoExt.Contains(ext)) return FileKind.Video;
        if (AudioExt.Contains(ext)) return FileKind.Audio;
        if (ArchiveExt.Contains(ext)) return FileKind.Archive;
        if (DocumentExt.Contains(ext)) return FileKind.Document;
        if (SpreadsheetExt.Contains(ext)) return FileKind.Spreadsheet;
        if (PresentationExt.Contains(ext)) return FileKind.Presentation;
        return FileKind.Other;
    }

    public static string ModeTitle(WheelMode mode) => mode switch
    {
        WheelMode.Convert => "Конвертировать",
        WheelMode.Tools => "Инструменты",
        _ => "Действия",
    };

    public static List<WheelOption> For(WheelMode mode, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return new List<WheelOption>();
        }

        FileKind kind = KindOf(files[0]);
        string sourceExt = ExtOf(files[0]);

        return mode switch
        {
            WheelMode.Convert => ConvertOptions(kind)
                .Where(o => o.Target != sourceExt)
                .ToList(),
            WheelMode.Tools => ToolOptions(kind, files.Count).ToList(),
            _ => ActionOptions().ToList(),
        };
    }

    // Every option there is, for looking one up by id from the command line.
    public static IEnumerable<WheelOption> All()
    {
        foreach (FileKind kind in Enum.GetValues<FileKind>())
        {
            foreach (var option in ConvertOptions(kind)) yield return option;
            foreach (var option in ToolOptions(kind, 2)) yield return option;
        }
        foreach (var option in ActionOptions()) yield return option;
    }

    static IEnumerable<WheelOption> ConvertOptions(FileKind kind)
    {
        switch (kind)
        {
            case FileKind.Image:
                yield return Conv("jpg", "JPG", "фото", FileKind.Image, false);
                yield return Conv("png", "PNG", "без потерь", FileKind.Image, false);
                yield return Conv("webp", "WEBP", "лёгкий", FileKind.Image, true);
                yield return Conv("avif", "AVIF", "новый", FileKind.Image, true);
                yield return Conv("gif", "GIF", "", FileKind.Image, false);
                yield return Conv("bmp", "BMP", "", FileKind.Image, false);
                yield return Conv("tiff", "TIFF", "печать", FileKind.Image, false);
                yield return Conv("ico", "ICO", "значок", FileKind.Image, false);
                break;

            case FileKind.Video:
                yield return Conv("mp4", "MP4", "везде", FileKind.Video, true);
                yield return Conv("mkv", "MKV", "", FileKind.Video, true);
                yield return Conv("webm", "WEBM", "для веба", FileKind.Video, true);
                yield return Conv("mov", "MOV", "Apple", FileKind.Video, true);
                yield return Conv("avi", "AVI", "", FileKind.Video, true);
                yield return Conv("gif", "GIF", "анимация", FileKind.Video, true);
                yield return Conv("mp3", "MP3", "звук", FileKind.Video, true);
                yield return Conv("m4a", "M4A", "звук", FileKind.Video, true);
                yield return Conv("wav", "WAV", "звук", FileKind.Video, true);
                break;

            case FileKind.Audio:
                yield return Conv("mp3", "MP3", "везде", FileKind.Audio, true);
                yield return Conv("m4a", "M4A", "AAC", FileKind.Audio, true);
                yield return Conv("wav", "WAV", "", FileKind.Audio, true);
                yield return Conv("flac", "FLAC", "без потерь", FileKind.Audio, true);
                yield return Conv("ogg", "OGG", "Vorbis", FileKind.Audio, true);
                yield return Conv("opus", "OPUS", "голос", FileKind.Audio, true);
                yield return Conv("aiff", "AIFF", "", FileKind.Audio, true);
                break;

            case FileKind.Archive:
                yield return Conv("zip", "ZIP", "", FileKind.Archive, false);
                yield return Conv("tar.gz", "TAR.GZ", "", FileKind.Archive, false);
                break;

            case FileKind.Document:
                yield return Conv("docx", "DOCX", "Word", FileKind.Document, false);
                yield return Conv("pdf", "PDF", "", FileKind.Document, false);
                yield return Conv("doc", "DOC", "старый Word", FileKind.Document, false);
                yield return Conv("rtf", "RTF", "", FileKind.Document, false);
                yield return Conv("odt", "ODT", "OpenDocument", FileKind.Document, false);
                yield return Conv("html", "HTML", "веб-страница", FileKind.Document, false);
                yield return Conv("txt", "TXT", "текст", FileKind.Document, false);
                break;

            case FileKind.Spreadsheet:
                yield return Conv("xlsx", "XLSX", "Excel", FileKind.Spreadsheet, false);
                yield return Conv("pdf", "PDF", "", FileKind.Spreadsheet, false);
                yield return Conv("csv", "CSV", "1-й лист", FileKind.Spreadsheet, false);
                yield return Conv("xls", "XLS", "старый Excel", FileKind.Spreadsheet, false);
                yield return Conv("ods", "ODS", "OpenDocument", FileKind.Spreadsheet, false);
                break;

            case FileKind.Presentation:
                yield return Conv("pptx", "PPTX", "PowerPoint", FileKind.Presentation, false);
                yield return Conv("pdf", "PDF", "", FileKind.Presentation, false);
                yield return Conv("ppt", "PPT", "старый", FileKind.Presentation, false);
                yield return Conv("odp", "ODP", "OpenDocument", FileKind.Presentation, false);
                break;
        }
    }

    static IEnumerable<WheelOption> ToolOptions(FileKind kind, int fileCount)
    {
        switch (kind)
        {
            case FileKind.Image:
                yield return Tool("compress", "Сжать", "", "", FileKind.Image, false);
                yield return Tool("half", "Уменьшить", "вдвое", "", FileKind.Image, false);
                yield return Tool("rotate", "Повернуть", "на 90°", "", FileKind.Image, false);
                yield return Tool("flip", "Отразить", "", "", FileKind.Image, false);
                yield return Tool("gray", "Ч/Б", "", "", FileKind.Image, false);
                yield return Tool("strip", "Без EXIF", "метаданные", "", FileKind.Image, false);
                break;

            case FileKind.Video:
                yield return Tool("vcompress", "Сжать", "", "", FileKind.Video, true);
                yield return Tool("720p", "720p", "уменьшить", "", FileKind.Video, true);
                yield return Tool("mute", "Без звука", "", "", FileKind.Video, true);
                yield return Tool("vrotate", "Повернуть", "на 90°", "", FileKind.Video, true);
                yield return Tool("frame", "Кадр", "в PNG", "", FileKind.Video, true);
                yield return Tool("speed2", "Ускорить", "×2", "", FileKind.Video, true);
                break;

            case FileKind.Audio:
                yield return Tool("acompress", "Сжать", "MP3 128k", "", FileKind.Audio, true);
                yield return Tool("normalize", "Громкость", "выровнять", "", FileKind.Audio, true);
                yield return Tool("mono", "Моно", "", "", FileKind.Audio, true);
                yield return Tool("trimsilence", "Тишина", "обрезать", "", FileKind.Audio, true);
                break;

            case FileKind.Archive:
                yield return Tool("extract", "Распаковать", "", "", FileKind.Archive, false);
                break;
        }

        yield return Tool("zip", "В ZIP", fileCount > 1 ? "всё вместе" : "", "", null, false);
    }

    static IEnumerable<WheelOption> ActionOptions()
    {
        yield return new WheelOption("act:copypath", "Путь", "копировать", "", null, false);
        yield return new WheelOption("act:copy", "Копировать", "в буфер", "", null, false);
        yield return new WheelOption("act:float", "Поверх окон", "закрепить", "", null, false);
        yield return new WheelOption("act:terminal", "Терминал", "здесь", "", null, false);
        yield return new WheelOption("act:share", "Поделиться", "", "", null, false);
        yield return new WheelOption("act:reveal", "В папке", "показать", "", null, false);
    }

    static WheelOption Conv(string target, string label, string sub, FileKind kind, bool ffmpeg) =>
        new("conv:" + target, label, sub, "", kind, ffmpeg);

    static WheelOption Tool(string id, string label, string sub, string glyph, FileKind? kind, bool ffmpeg) =>
        new("tool:" + id, label, sub, glyph, kind, ffmpeg);
}
