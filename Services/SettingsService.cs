using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using MusicEdge.Models;

namespace MusicEdge.Services;

public static class SettingsService
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MusicEdgeX";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicEdgeX");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            Normalize(settings);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Normalize(settings);
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            ApplyAutoStart(settings.AutoStart);
        }
        catch
        {
            // Settings must never prevent the app from running.
        }
    }

    public static void ApplyAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (key == null) return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                    key.SetValue(RunValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, false);
            }
        }
        catch
        {
            // Registry access can be blocked by policy.
        }
    }

    private static void Normalize(AppSettings settings)
    {
        settings.EdgeSide = string.Equals(settings.EdgeSide, "Left", StringComparison.OrdinalIgnoreCase)
            ? "Left"
            : "Right";
        settings.HoverDelayMs = Math.Clamp(settings.HoverDelayMs, 100, 1200);
        settings.CollapseDelayMs = Math.Clamp(settings.CollapseDelayMs, 200, 3000);
        settings.HideDelayMs = Math.Clamp(settings.HideDelayMs, settings.CollapseDelayMs + 200, 6000);
        settings.PlayerCapHost = string.IsNullOrWhiteSpace(settings.PlayerCapHost)
            ? "127.0.0.1:8765"
            : settings.PlayerCapHost.Trim();
        settings.PlayerCapPlayer = settings.PlayerCapPlayer?.Trim() ?? string.Empty;
    }
}
