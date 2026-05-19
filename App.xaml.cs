using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using MusicEdge.Services;

namespace MusicEdge;

/// <summary>
/// Application entry point with system tray icon support (using pure Win32 Shell_NotifyIcon).
/// The main window starts hidden and is shown when the mouse approaches the screen edge.
/// </summary>
public partial class App : Application
{
    // Win32 Shell_NotifyIcon declarations
    [DllImport("shell32.dll")]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIF_GUID = 0x00000020;
    private const uint NIIF_INFO = 0x00000001;
    private const uint NOTIFYICON_VERSION_4 = 4;

    private const int WM_TRAYICON = 0x8000;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_COMMAND = 0x0111;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    // Context menu via Win32
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool InsertMenu(IntPtr hMenu, uint uPosition, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lpTPMParams);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint CMD_SHOW = 1001;
    private const uint CMD_HIDE = 1002;
    private const uint CMD_SETTINGS = 1003;
    private const uint CMD_EXIT = 1004;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private MainWindow? _mainWindow;
    private Mutex? _mutex;
    private IntPtr _trayIconHandle = IntPtr.Zero;
    private bool _trayIconAdded;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        try
        {
            Console.WriteLine("[MusicEdge] === STARTUP BEGIN ===");

            _mutex = new Mutex(true, "MusicEdge_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("MusicEdge 已在运行中。",
                    "MusicEdge", MessageBoxButton.OK, MessageBoxImage.Information);
                this.Shutdown();
                return;
            }

            Console.WriteLine("[MusicEdge] Creating MainWindow...");
            _mainWindow = new MainWindow();
            _mainWindow.Show();

            var helper = new System.Windows.Interop.WindowInteropHelper(_mainWindow);
            IntPtr hwnd = helper.EnsureHandle();
            Console.WriteLine($"[MusicEdge] MainWindow HWND=0x{hwnd:X}");

            AddTrayIcon();
            Console.WriteLine("[MusicEdge] Tray icon added");

            if (!QQMusicHotkeyService.IsQQMusicRunning())
                ShowTrayBalloon("MusicEdge 已启动", "鼠标移到屏幕右边缘即可显示面板");

            Console.WriteLine("[MusicEdge] === STARTUP COMPLETE ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MusicEdge] FATAL STARTUP ERROR: {ex}");
            MessageBox.Show($"启动失败:\n{ex}", "MusicEdge Error");
        }
    }

    private IntPtr GetTrayOwnerHandle()
    {
        if (_mainWindow != null)
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(_mainWindow);
            return helper.EnsureHandle();
        }
        return IntPtr.Zero;
    }

    #region Tray Icon

    private void AddTrayIcon()
    {
        var hwnd = GetTrayOwnerHandle();
        if (hwnd == IntPtr.Zero) return;

        var icon = CreateMusicNoteIcon();
        _trayIconHandle = icon.Handle;

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _trayIconHandle,
            szTip = "MusicEdge - QQ音乐边缘控制"
        };

        Shell_NotifyIcon(NIM_ADD, ref nid);

        nid.uVersionOrTimeout = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref nid);

        _trayIconAdded = true;

        var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
        if (source != null)
        {
            source.AddHook(WndProcHook);
            Console.WriteLine("[MusicEdge] HwndSource hook added");
        }
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            if (msg == WM_TRAYICON)
            {
                uint lParamU = (uint)lParam.ToInt32();
                Console.WriteLine($"[MusicEdge] Tray msg: lParam=0x{lParamU:X}");

                if (lParamU == WM_LBUTTONDBLCLK)
                {
                    Console.WriteLine("[MusicEdge] Double-click → ShowPanel");
                    ShowPanel();
                }
                else if (lParamU == WM_RBUTTONUP)
                {
                    Console.WriteLine("[MusicEdge] Right-click → ShowContextMenu");
                    ShowContextMenu();
                }
                handled = true;
            }
            else if (msg == WM_COMMAND)
            {
                uint cmd = (uint)wParam.ToInt32() & 0xFFFF;
                Console.WriteLine($"[MusicEdge] WM_COMMAND cmd={cmd}");
                switch (cmd)
                {
                    case CMD_SHOW: ShowPanel(); break;
                    case CMD_HIDE: HidePanel(); break;
                    case CMD_SETTINGS: OpenSettings(); break;
                    case CMD_EXIT: Dispatcher.Invoke(() => this.Shutdown()); break;
                }
                handled = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MusicEdge] WndProcHook ERROR: {ex}");
        }
        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        Console.WriteLine("[MusicEdge] ShowContextMenu called");
        var hMenu = CreatePopupMenu();

        AppendMenu(hMenu, MF_STRING, CMD_SHOW, "显示面板");
        AppendMenu(hMenu, MF_STRING, CMD_HIDE, "隐藏面板");
        AppendMenu(hMenu, MF_SEPARATOR, 0, "");
        AppendMenu(hMenu, MF_STRING, CMD_SETTINGS, "设置");
        AppendMenu(hMenu, MF_SEPARATOR, 0, "");
        AppendMenu(hMenu, MF_STRING, CMD_EXIT, "退出");

        GetCursorPos(out POINT pt);
        SetForegroundWindow(GetTrayOwnerHandle());

        uint cmd = TrackPopupMenuEx(hMenu,
            TPM_RETURNCMD | TPM_BOTTOMALIGN | TPM_LEFTALIGN | TPM_RIGHTBUTTON,
            pt.X, pt.Y, GetTrayOwnerHandle(), IntPtr.Zero);
        DestroyMenu(hMenu);

        Console.WriteLine($"[MusicEdge] Menu returned cmd={cmd} (0= dismissed, {CMD_SHOW}=Show, {CMD_HIDE}=Hide, {CMD_SETTINGS}=Settings, {CMD_EXIT}=Exit)");

        switch (cmd)
        {
            case 0: break; // dismissed
            case CMD_SHOW: ShowPanel(); break;
            case CMD_HIDE: HidePanel(); break;
            case CMD_SETTINGS: OpenSettings(); break;
            case CMD_EXIT: Dispatcher.Invoke(() => this.Shutdown()); break;
        }
    }

    #endregion

    #region Actions

    private void ShowPanel()
    {
        Console.WriteLine("[MusicEdge] ShowPanel called");
        _mainWindow?.ShowPanelFromTray();
    }

    private void HidePanel()
    {
        Console.WriteLine("[MusicEdge] HidePanel called");
        _mainWindow?.HidePanelFromTray();
    }

    private void OpenSettings()
    {
        if (_mainWindow == null) return;

        Dispatcher.Invoke(() =>
        {
            var settingsWindow = new SettingsWindow(new Services.EdgeDetector())
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            settingsWindow.ShowDialog();
        });
    }

    #endregion

    #region Helpers

    private Icon CreateMusicNoteIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(26, 26, 46));

        using var pen = new Pen(Color.FromArgb(233, 69, 96), 2);
        using var brush = new SolidBrush(Color.FromArgb(233, 69, 96));

        g.DrawLine(pen, 4, 13, 4, 6);
        g.DrawLine(pen, 4, 6, 10, 4);
        g.DrawLine(pen, 10, 4, 10, 9);
        g.FillEllipse(brush, 2, 11, 5, 5);
        g.FillEllipse(brush, 8, 7, 5, 5);

        IntPtr hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    private void ShowTrayBalloon(string title, string message)
    {
        if (!_trayIconAdded) return;

        var hwnd = GetTrayOwnerHandle();
        if (hwnd == IntPtr.Zero) return;

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_INFO,
            szInfo = message,
            szInfoTitle = title,
            dwInfoFlags = NIIF_INFO,
            uVersionOrTimeout = 3000
        };

        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    #endregion

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        if (_trayIconAdded)
        {
            var hwnd = GetTrayOwnerHandle();
            if (hwnd != IntPtr.Zero)
            {
                var nid = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = hwnd,
                    uID = 1
                };
                Shell_NotifyIcon(NIM_DELETE, ref nid);
            }
        }

        if (_trayIconHandle != IntPtr.Zero)
            DestroyIcon(_trayIconHandle);

        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}

/// <summary>
/// Simple Win32 helpers for screen metrics.
/// </summary>
internal static class Win32Helper
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    public static int GetScreenWidth() => GetSystemMetrics(SM_CXSCREEN);
    public static int GetScreenHeight() => GetSystemMetrics(SM_CYSCREEN);
}
