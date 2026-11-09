using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using MusicEdge.Models;
using MusicEdge.Services;

namespace MusicEdge;

public partial class App : Application
{
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIIF_INFO = 0x00000001;
    private const uint NOTIFYICON_VERSION_4 = 4;

    private const int WM_TRAYICON = 0x8001;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;

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
    private const uint CMD_PLAY_PAUSE = 1005;
    private const uint CMD_NEXT = 1006;
    private const uint CMD_PREVIOUS = 1007;
    private const uint CMD_LIKE = 1008;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private MainWindow? _mainWindow;
    private Mutex? _mutex;
    private Icon? _trayIcon;
    private IntPtr _trayIconHandle;
    private bool _trayIconAdded;
    private uint _showPanelMessage;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        _showPanelMessage = RegisterWindowMessage("MusicEdgeX.ShowPanel");

        _mutex = new Mutex(true, "MusicEdgeX_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            if (_showPanelMessage != 0)
                PostMessage(HWND_BROADCAST, _showPanelMessage, IntPtr.Zero, IntPtr.Zero);

            Shutdown();
            return;
        }

        try
        {
            var settings = SettingsService.Load();
            SettingsService.ApplyAutoStart(settings.AutoStart);

            var args = Environment.GetCommandLineArgs();
            bool showOnStart = args.Any(arg => string.Equals(arg, "--show", StringComparison.OrdinalIgnoreCase));
            bool openSettings = args.Any(arg => string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase));

            _mainWindow = new MainWindow(settings, showOnStart);
            _mainWindow.Show();
            AddTrayIcon();

            if (openSettings)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(() => _mainWindow?.OpenSettingsDialog()));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败:\n{ex.Message}", "MusicEdgeX",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private IntPtr GetTrayOwnerHandle()
    {
        if (_mainWindow == null) return IntPtr.Zero;
        return new System.Windows.Interop.WindowInteropHelper(_mainWindow).EnsureHandle();
    }

    #region Tray icon

    private void AddTrayIcon()
    {
        var hwnd = GetTrayOwnerHandle();
        if (hwnd == IntPtr.Zero) return;

        _trayIcon = LoadTrayIcon();
        _trayIconHandle = _trayIcon.Handle;

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _trayIconHandle,
            szTip = "MusicEdgeX · 边缘音乐控制"
        };

        _trayIconAdded = Shell_NotifyIcon(NIM_ADD, ref nid);
        if (!_trayIconAdded) return;

        nid.uVersionOrTimeout = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref nid);

        var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProcHook);
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon != null) return icon;
            }
        }
        catch { }

        return CreateFallbackIcon();
    }

    private static Icon CreateFallbackIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(System.Drawing.Color.Transparent);

        using var background = new SolidBrush(System.Drawing.Color.FromArgb(20, 20, 32));
        graphics.FillEllipse(background, 1, 1, 30, 30);

        using var accent = new SolidBrush(System.Drawing.Color.FromArgb(139, 123, 255));
        using var pen = new Pen(accent, 2.4f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };
        graphics.DrawLine(pen, 13, 24, 13, 10);
        graphics.DrawLine(pen, 13, 10, 22, 8);
        graphics.DrawLine(pen, 22, 8, 22, 16);
        graphics.FillEllipse(accent, 8, 21, 8, 6);
        graphics.FillEllipse(accent, 17, 13, 8, 6);

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            if (_showPanelMessage != 0 && msg == _showPanelMessage)
            {
                ShowPanel();
                handled = true;
                return IntPtr.Zero;
            }

            if (msg != WM_TRAYICON) return IntPtr.Zero;

            int notification = lParam.ToInt32() & 0xFFFF;
            switch (notification)
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    ShowPanel();
                    break;
                case (int)WM_LBUTTONDBLCLK:
                    ShowPanel();
                    break;
                case (int)WM_RBUTTONUP:
                case (int)WM_CONTEXTMENU:
                    ShowContextMenu();
                    break;
            }

            handled = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, CMD_SHOW, "显示面板");
        AppendMenu(menu, MF_STRING, CMD_HIDE, "隐藏面板");
        AppendMenu(menu, MF_SEPARATOR, 0, string.Empty);
        AppendMenu(menu, MF_STRING, CMD_PLAY_PAUSE, "播放 / 暂停");
        AppendMenu(menu, MF_STRING, CMD_PREVIOUS, "上一首");
        AppendMenu(menu, MF_STRING, CMD_NEXT, "下一首");
        AppendMenu(menu, MF_STRING, CMD_LIKE, "收藏（QQ 音乐）");
        AppendMenu(menu, MF_SEPARATOR, 0, string.Empty);
        AppendMenu(menu, MF_STRING, CMD_SETTINGS, "设置");
        AppendMenu(menu, MF_SEPARATOR, 0, string.Empty);
        AppendMenu(menu, MF_STRING, CMD_EXIT, "退出");

        GetCursorPos(out POINT point);
        var owner = GetTrayOwnerHandle();
        SetForegroundWindow(owner);

        uint command = TrackPopupMenuEx(menu,
            TPM_RETURNCMD | TPM_BOTTOMALIGN | TPM_LEFTALIGN | TPM_RIGHTBUTTON,
            point.X, point.Y, owner, IntPtr.Zero);
        DestroyMenu(menu);

        switch (command)
        {
            case CMD_SHOW: ShowPanel(); break;
            case CMD_HIDE: HidePanel(); break;
            case CMD_PLAY_PAUSE: _mainWindow?.TogglePlayPauseFromTray(); break;
            case CMD_PREVIOUS: _mainWindow?.PreviousFromTray(); break;
            case CMD_NEXT: _mainWindow?.NextFromTray(); break;
            case CMD_LIKE: _mainWindow?.ToggleLikeFromTray(); break;
            case CMD_SETTINGS: OpenSettings(); break;
            case CMD_EXIT: Shutdown(); break;
        }
    }

    private void ShowPanel() => _mainWindow?.ShowPanelFromTray();

    private void HidePanel() => _mainWindow?.HidePanelFromTray();

    private void OpenSettings() => _mainWindow?.OpenSettingsDialog();

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

        _trayIcon?.Dispose();
        _trayIcon = null;

        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _mutex = null;
    }

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint item, string text);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
