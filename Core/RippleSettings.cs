using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Ripple.Core;

public enum WallpaperFit
{
    Fill,
    Fit,
    Stretch,
    Centre,
}

public enum WaterDetail
{
    Low,
    Normal,
    High,
}

/// <summary>Everything Ripple remembers between runs.</summary>
public sealed record RippleSettings
{
    /// <summary>The chosen picture, or null to follow the Windows wallpaper.</summary>
    public string? WallpaperPath { get; init; }
    public WallpaperFit Fit { get; init; } = WallpaperFit.Fill;

    /// <summary>All three run from 0 to 1.</summary>
    public double Strength { get; init; } = 0.5;
    public double Damping { get; init; } = 0.35;
    public double Sensitivity { get; init; } = 0.5;

    public bool ClickSplash { get; init; } = true;
    public bool PauseOnFullScreen { get; init; } = true;
    public bool PauseOnBattery { get; init; }
    public bool Paused { get; init; }
    public WaterDetail Detail { get; init; } = WaterDetail.Normal;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ripple", "settings.json");

    public static RippleSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<RippleSettings>(File.ReadAllText(FilePath)) ?? new RippleSettings();
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Info($"Settings could not be read, using defaults: {exception.Message}");
        }

        return new RippleSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Info($"Settings could not be saved: {exception.Message}");
        }
    }
}

/// <summary>The "start with Windows" switch, kept in the normal per-user Run key.</summary>
public static class StartWithWindows
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Ripple";

    public static bool IsOn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(Name) is string;
        }
    }

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (on)
        {
            key.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
        }
        else
        {
            key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
