using System.Runtime.InteropServices;

namespace MusicEdge.Services;

// Shared keyboard input primitives for the QQ Music shortcut.
internal static class MediaKeyService
{
    private const uint INPUT_KEYBOARD = 1;

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
