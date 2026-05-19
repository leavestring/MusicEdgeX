using System.Runtime.InteropServices;

namespace MusicEdge.Services;

/// <summary>
/// Simulates media key presses (Play/Pause, Next, Previous) using Windows keybd_event API.
/// This is more reliable than SMTC for controlling QQ Music.
/// </summary>
public class MediaKeyService
{
    #region Keybd_event Interop

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // Virtual Key Codes for media keys
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_STOP = 0xB2;
    private const byte VK_VOLUME_UP = 0xAF;
    private const byte VK_VOLUME_DOWN = 0xAE;

    #endregion

    private const int KeyPressDelayMs = 30; // Delay between key down and key up

    /// <summary>
    /// Toggle Play/Pause.
    /// </summary>
    public void TogglePlayPause()
    {
        keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        Thread.Sleep(KeyPressDelayMs);
        keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>
    /// Skip to next track.
    /// </summary>
    public void NextTrack()
    {
        keybd_event(VK_MEDIA_NEXT_TRACK, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        Thread.Sleep(KeyPressDelayMs);
        keybd_event(VK_MEDIA_NEXT_TRACK, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>
    /// Go to previous track.
    /// </summary>
    public void PreviousTrack()
    {
        keybd_event(VK_MEDIA_PREV_TRACK, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        Thread.Sleep(KeyPressDelayMs);
        keybd_event(VK_MEDIA_PREV_TRACK, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>
    /// Volume up (5% step).
    /// </summary>
    public void VolumeUp()
    {
        keybd_event(VK_VOLUME_UP, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        Thread.Sleep(KeyPressDelayMs);
        keybd_event(VK_VOLUME_UP, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>
    /// Volume down (5% step).
    /// </summary>
    public void VolumeDown()
    {
        keybd_event(VK_VOLUME_DOWN, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        Thread.Sleep(KeyPressDelayMs);
        keybd_event(VK_VOLUME_DOWN, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
