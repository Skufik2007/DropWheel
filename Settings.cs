using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DropWheel;

internal sealed class Settings
{
    // Which modifier opens which wheel: "Shift", "Ctrl" or "Alt".
    public string ConvertKey { get; set; } = "Shift";
    public string ToolsKey { get; set; } = "Ctrl";
    public string ActionsKey { get; set; } = "Alt";

    // Only react to drags that start in File Explorer or on the desktop, so
    // Shift+drag in other programs keeps working as usual.
    public bool ExplorerOnly { get; set; } = true;

    public int JpegQuality { get; set; } = 92;
    public string FfmpegPath { get; set; } = "";

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropWheel");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Current { get; private set; } = new();

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
        }
        catch (Exception ex)
        {
            Log.Write("Settings load failed: " + ex.Message);
            Current = new Settings();
        }
        Save();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Write("Settings save failed: " + ex.Message);
        }
    }
}

internal static class Log
{
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Folder);
                File.AppendAllText(Path.Combine(Settings.Folder, "log.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
