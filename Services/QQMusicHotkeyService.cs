using System.Diagnostics;
using System.IO;
using System.Windows;

namespace MusicEdge.Services;

/// <summary>
/// QQ Music specific operations that SMTC does not expose.
/// </summary>
public sealed class QQMusicHotkeyService
{
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_F = 0x46;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const int SW_RESTORE = 9;

    /// <summary>
    /// Triggers QQ Music's global "like" shortcut (Ctrl+Alt+F) only when the
    /// player is actually running, so the chord never leaks into other apps.
    /// </summary>
    public bool ToggleLike()
    {
        if (!IsQQMusicRunning()) return false;

        MediaKeyService.SendInputs(
            MediaKeyService.CreateKey(VK_CONTROL, 0),
            MediaKeyService.CreateKey(VK_MENU, 0),
            MediaKeyService.CreateKey(VK_F, 0),
            MediaKeyService.CreateKey(VK_F, KEYEVENTF_KEYUP),
            MediaKeyService.CreateKey(VK_MENU, KEYEVENTF_KEYUP),
            MediaKeyService.CreateKey(VK_CONTROL, KEYEVENTF_KEYUP));

        return true;
    }

    public static bool IsQQMusicRunning()
    {
        var processes = Process.GetProcessesByName("QQMusic");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public static bool LaunchQQMusic()
    {
        foreach (var path in GetCandidatePaths())
        {
            if (!File.Exists(path)) continue;

            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                return true;
            }
            catch { }
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = "QQMusic.exe", UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool BringQQMusicToFront()
    {
        var processes = Process.GetProcessesByName("QQMusic");
        try
        {
            foreach (var process in processes)
            {
                if (process.MainWindowHandle == IntPtr.Zero) continue;

                ShowWindow(process.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(process.MainWindowHandle);
                return true;
            }
            return false;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public static bool OpenPlayer(string sourceAppId, string sourceName)
    {
        if (sourceName == "QQ音乐" || sourceAppId.Contains("QQMusic", StringComparison.OrdinalIgnoreCase))
            return BringQQMusicToFront() || LaunchQQMusic();

        if (sourceName == "Spotify" || sourceAppId.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
            return TryStart("spotify:");

        if (sourceName == "网易云音乐" || sourceAppId.Contains("CloudMusic", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var path in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             @"Netease\CloudMusic\cloudmusic.exe"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                             @"Netease\CloudMusic\cloudmusic.exe"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             @"Netease\CloudMusic\cloudmusic.exe")
                     })
            {
                if (TryStart(path)) return true;
            }
        }

        return false;
    }

    private static bool TryStart(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Tencent\QQMusic\QQMusic.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            @"Tencent\QQMusic\QQMusic.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Tencent\QQMusic\QQMusic.exe");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
