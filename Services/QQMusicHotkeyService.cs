using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MusicEdge.Services;

/// <summary>
/// QQ Music specific operations that cannot be done via SMTC/media keys.
/// Currently: "Like/Favorite" functionality via keyboard shortcut simulation.
/// QQ Music default shortcut for "Like" is Ctrl+Alt+F (may vary by version).
/// </summary>
public class QQMusicHotkeyService
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_MENU = 0x12;    // Alt
    private const byte VK_F = 0x46;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    /// <summary>
    /// Simulates Ctrl+Alt+F (QQ Music default "Like" shortcut).
    /// This sends the keyboard shortcut globally.
    /// </summary>
    public void ToggleLike()
    {
        // Press Ctrl+Alt+F
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        Thread.Sleep(20);
        keybd_event(VK_F, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);

        // Release in reverse order
        keybd_event(VK_F, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>
    /// Check if QQ Music process is running.
    /// </summary>
    public static bool IsQQMusicRunning()
    {
        var processes = Process.GetProcessesByName("QQMusic");
        return processes.Length > 0;
    }

    /// <summary>
    /// Try to launch QQ Music.
    /// </summary>
    public static void LaunchQQMusic()
    {
        try
        {
            // Common QQ Music install paths
            string[] possiblePaths =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    @"Tencent\QQMusic\QQMusic.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    @"Tencent\QQMusic\QQMusic.exe"),
            ];

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                    return;
                }
            }

            // Fallback: try to find it
            Process.Start(new ProcessStartInfo
            {
                FileName = "QQMusic.exe",
                UseShellExecute = true
            });
        }
        catch
        {
            // Could not launch
        }
    }

    /// <summary>
    /// Bring QQ Music window to foreground.
    /// </summary>
    public static void BringQQMusicToFront()
    {
        try
        {
            var handle = FindWindow(null, "QQ音乐");
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SW_RESTORE);
                SetForegroundWindow(handle);
            }
        }
        catch { }
    }
}
