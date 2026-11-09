using System.Runtime.InteropServices;

namespace MusicEdge.Services;

/// <summary>
/// Sends media keys with a single non-blocking SendInput call.
/// Used as a fallback when the active SMTC session rejects a command.
/// </summary>
public sealed class MediaKeyService
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
    private const ushort VK_MEDIA_PREV_TRACK = 0xB1;
    private const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const ushort VK_VOLUME_UP = 0xAF;
    private const ushort VK_VOLUME_DOWN = 0xAE;

    public void TogglePlayPause() => SendKey(VK_MEDIA_PLAY_PAUSE, extended: true);

    public void NextTrack() => SendKey(VK_MEDIA_NEXT_TRACK, extended: true);

    public void PreviousTrack() => SendKey(VK_MEDIA_PREV_TRACK, extended: true);

    public void VolumeUp() => SendKey(VK_VOLUME_UP, extended: true);

    public void VolumeDown() => SendKey(VK_VOLUME_DOWN, extended: true);

    private static void SendKey(ushort virtualKey, bool extended)
    {
        var flags = extended ? KEYEVENTF_EXTENDEDKEY : 0;
        var inputs = new[]
        {
            CreateKey(virtualKey, flags),
            CreateKey(virtualKey, flags | KEYEVENTF_KEYUP)
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    internal static INPUT CreateKey(ushort virtualKey, uint flags) => new()
    {
        Type = INPUT_KEYBOARD,
        Data = new INPUTUNION
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = virtualKey,
                Flags = flags
            }
        }
    };

    internal static void SendInputs(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint Type;
        public INPUTUNION Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT Mouse;

        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, INPUT[] inputs, int size);
}
