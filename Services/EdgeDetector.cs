using System.Runtime.InteropServices;
using System.Windows.Threading;
using static MusicEdge.Helpers.Win32Interop;

namespace MusicEdge.Services;

/// <summary>
/// Event-driven screen-edge detector. A low-level mouse hook runs on its own
/// thread, so the app consumes no polling wakeups while the mouse is idle.
/// A short dispatcher timer only runs while the panel is actually visible.
/// </summary>
public sealed class EdgeDetector : IDisposable
{
    public event Action? MouseNearEdge;
    public event Action<bool>? MouseOverPanelChanged;
    public event Action? OnHoverLongEnough;
    public event Action? OnShouldCollapse;
    public event Action? OnShouldFullyHide;

    public int EdgeHotZoneWidth { get; set; } = 14;
    public int HoverDelayMs { get; set; } = 300;
    public int CollapseDelayMs { get; set; } = 650;
    public int HideDelayMs { get; set; } = 1400;
    public bool IsEnabled { get; set; } = true;
    public string EdgeSide { get; set; } = "Right";
    public IntPtr PanelHandle { get; set; }

    private Dispatcher? _dispatcher;
    private DispatcherTimer? _delayTimer;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle;
    private LowLevelMouseProc? _hookProc;
    private readonly ManualResetEventSlim _hookReady = new(false);
    private int _evaluatePending;
    private volatile bool _running;
    private bool _disposed;

    private readonly object _pointGate = new();
    private POINT _lastPoint;

    private bool _isNearEdge;
    private bool _isOverPanel;
    private bool _panelVisible;
    private bool _hoverPending;
    private bool _pinned;
    private bool _collapseNotified;
    private bool _hideNotified;
    private DateTime _hoverStarted = DateTime.MinValue;
    private DateTime _lastInside = DateTime.MinValue;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _delayTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _delayTimer.Tick += OnDelayTick;

        _hookProc = HookCallback;
        _hookThread = new Thread(HookThreadProc)
        {
            IsBackground = true,
            Name = "MusicEdge.MouseHook",
            Priority = ThreadPriority.BelowNormal
        };
        _hookThread.Start();

        if (!_hookReady.Wait(1500))
            StartPollingFallback();
        else if (_hookHandle == IntPtr.Zero)
            StartPollingFallback();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _dispatcher?.Invoke(() => _delayTimer?.Stop());

        if (_hookThreadId != 0)
            PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        if (_hookThread != null && _hookThread.IsAlive)
            _hookThread.Join(1500);

        _hookThread = null;
        _hookThreadId = 0;
        _hookHandle = IntPtr.Zero;
        _delayTimer = null;
    }

    public void NotifyPreviewShown()
    {
        _panelVisible = true;
        _hoverPending = true;
        _collapseNotified = false;
        _hideNotified = false;
        _hoverStarted = DateTime.UtcNow;
        _lastInside = DateTime.UtcNow;
        StartDelayTimer();
    }

    public void NotifyPanelExpanded()
    {
        _panelVisible = true;
        _hoverPending = false;
        _collapseNotified = false;
        _hideNotified = false;
        _lastInside = DateTime.UtcNow;
        StartDelayTimer();
    }

    public void NotifyPreviewCollapsed()
    {
        _panelVisible = true;
        _hoverPending = false;
        _collapseNotified = false;
        _hideNotified = false;
        _lastInside = DateTime.UtcNow;
        StartDelayTimer();
    }

    public void NotifyPanelHidden()
    {
        _panelVisible = false;
        _hoverPending = false;
        _isOverPanel = false;
        _delayTimer?.Stop();
    }

    public void SetPinned(bool pinned)
    {
        _pinned = pinned;
        if (pinned)
        {
            _collapseNotified = true;
            _hideNotified = true;
            _delayTimer?.Stop();
        }
        else if (_panelVisible)
        {
            _collapseNotified = false;
            _hideNotified = false;
            StartDelayTimer();
        }
    }

    private void StartDelayTimer()
    {
        if (_delayTimer != null && !_delayTimer.IsEnabled)
            _delayTimer.Start();
    }

    private void HookThreadProc()
    {
        _hookThreadId = GetCurrentThreadId();
        _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _hookProc!, GetModuleHandle(null!), 0);
        _hookReady.Set();

        if (_hookHandle == IntPtr.Zero)
            return;

        while (_running && GetMessage(out MSG message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == WM_MOUSEMOVE)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            lock (_pointGate)
            {
                _lastPoint = data.pt;
            }

            if (Interlocked.Exchange(ref _evaluatePending, 1) == 0)
            {
                var dispatcher = _dispatcher;
                dispatcher?.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    POINT point;
                    lock (_pointGate)
                    {
                        point = _lastPoint;
                    }
                    Volatile.Write(ref _evaluatePending, 0);
                    Evaluate(point);
                }));
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private DispatcherTimer? _fallbackPoll;

    private void StartPollingFallback()
    {
        if (_dispatcher == null) return;
        _fallbackPoll = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _fallbackPoll.Tick += (_, _) => PollCursor();
        _fallbackPoll.Start();
    }

    private void PollCursor()
    {
        if (!_running || !GetCursorPos(out POINT point)) return;
        lock (_pointGate)
        {
            _lastPoint = point;
        }
        Evaluate(point);
    }

    private void Evaluate(POINT point)
    {
        if (_disposed || !IsEnabled || _pinned) return;

        bool near = IsNearEdge(point);
        bool over = IsOverPanel(point);
        var now = DateTime.UtcNow;

        if (near && !_isNearEdge)
        {
            _isNearEdge = true;
            MouseNearEdge?.Invoke();
        }
        else if (!near)
        {
            _isNearEdge = false;
        }

        if (over != _isOverPanel)
        {
            _isOverPanel = over;
            MouseOverPanelChanged?.Invoke(over);
        }

        if (near || over)
        {
            _lastInside = now;
            _collapseNotified = false;
            _hideNotified = false;
        }
        else if (_hoverPending)
            _hoverPending = false;

        if (_panelVisible)
            StartDelayTimer();
    }

    private void OnDelayTick(object? sender, EventArgs e)
    {
        if (_disposed || !IsEnabled) return;

        POINT point;
        lock (_pointGate)
        {
            point = _lastPoint;
        }

        bool near = IsNearEdge(point);
        bool over = IsOverPanel(point);
        var now = DateTime.UtcNow;

        if (over != _isOverPanel)
        {
            _isOverPanel = over;
            MouseOverPanelChanged?.Invoke(over);
        }

        _isNearEdge = near;
        if (near || over)
            _lastInside = now;

        if (_hoverPending)
        {
            if (!near && !over)
            {
                _hoverPending = false;
            }
            else if ((now - _hoverStarted).TotalMilliseconds >= HoverDelayMs)
            {
                _hoverPending = false;
                OnHoverLongEnough?.Invoke();
            }
        }

        if (!_panelVisible) return;

        if (!near && !over)
        {
            var elapsed = (now - _lastInside).TotalMilliseconds;
            if (elapsed >= HideDelayMs)
            {
                if (!_hideNotified)
                {
                    _hideNotified = true;
                    OnShouldFullyHide?.Invoke();
                }
            }
            else if (elapsed >= CollapseDelayMs)
            {
                if (!_collapseNotified)
                {
                    _collapseNotified = true;
                    OnShouldCollapse?.Invoke();
                }
            }
        }
    }

    private bool IsNearEdge(POINT point)
    {
        IntPtr monitor = PanelHandle != IntPtr.Zero
            ? MonitorFromWindow(PanelHandle, MONITOR_DEFAULTTONEAREST)
            : MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var work = info.rcWork;
        if (point.Y < work.Top || point.Y >= work.Bottom) return false;

        return EdgeSide == "Left"
            ? point.X <= work.Left + EdgeHotZoneWidth
            : point.X >= work.Right - EdgeHotZoneWidth;
    }

    private bool IsOverPanel(POINT point)
    {
        if (PanelHandle == IntPtr.Zero) return false;
        if (!GetWindowRect(PanelHandle, out RECT rect)) return false;

        return point.X >= rect.Left && point.X < rect.Right &&
               point.Y >= rect.Top && point.Y < rect.Bottom;
    }

    public static bool IsForegroundFullScreen(IntPtr ownWindow = default)
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) >= 0 &&
                state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
            {
                return true;
            }
        }
        catch { }

        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == ownWindow) return false;
            if (!GetWindowRect(foreground, out RECT window)) return false;

            var monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;

            var screen = info.rcMonitor;
            const int tolerance = 2;
            return window.Left <= screen.Left + tolerance &&
                   window.Top <= screen.Top + tolerance &&
                   window.Right >= screen.Right - tolerance &&
                   window.Bottom >= screen.Bottom - tolerance;
        }
        catch
        {
            return false;
        }
    }

    public static uint GetIdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info)
            ? (uint)(Environment.TickCount - info.dwTime) / 1000
            : 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _fallbackPoll?.Stop();
        _fallbackPoll = null;
        _hookReady.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
