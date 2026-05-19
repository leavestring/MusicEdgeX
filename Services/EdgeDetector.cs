using System.Runtime.InteropServices;
using MusicEdge.Helpers;
using static MusicEdge.Helpers.Win32Interop;

namespace MusicEdge.Services;

/// <summary>
/// Polling-based edge detector using WPF DispatcherTimer.
/// Much simpler and more reliable than WH_MOUSE_LL hook.
/// </summary>
public class EdgeDetector : IDisposable
{
    public event Action? MouseNearEdge;
    public event Action? MouseLeftEdge;
    public event Action<bool>? MouseOverPanelChanged;

    private bool _isNearEdge;
    private bool _isOverPanel;
    private DateTime _lastEdgeTime = DateTime.MinValue;
    private DateTime _lastLeaveTime = DateTime.MinValue;

    // Configuration
    public int EdgeHotZoneWidth { get; set; } = 12;
    public int PreviewPanelWidth { get; set; } = 48;
    public int FullPanelWidth { get; set; } = 360;
    public int HoverDelayMs { get; set; } = 300;
    public int HideDelayMs { get; set; } = 500;
    public int FullHideDelayMs { get; set; } = 800;
    public bool IsEnabled { get; set; } = true;

    // Panel screen position (set by MainWindow)
    public double PanelX { get; set; }
    public double PanelY { get; set; }
    public double PanelWidth { get; set; }
    public double PanelHeight { get; set; }
    public double PanelVisibleX { get; set; }

    private System.Windows.Threading.DispatcherTimer? _pollTimer;
    private DateTime _hoverTriggerTime = DateTime.MinValue;
    private bool _hoverPending;

    private int _pollCount;

    public event Action? OnHoverLongEnough;
    public event Action? OnShouldCollapse;
    public event Action? OnShouldFullyHide;

    // Cached physical screen dimensions (set once on Start)
    private int _screenWidth;
    private int _screenHeight;

    public void Start()
    {
        Console.WriteLine("[MusicEdge] EdgeDetector starting (polling mode)...");

        _screenWidth = GetSystemMetrics(0);
        _screenHeight = GetSystemMetrics(1);

        // Poll every 50ms — 20 times/sec, negligible CPU
        _pollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _pollTimer.Tick += PollTick;
        _pollTimer.Start();

        Console.WriteLine($"[MusicEdge] Screen={_screenWidth}x{_screenHeight}, HotZone={EdgeHotZoneWidth}px");
    }

    public void Stop()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        Console.WriteLine($"[MusicEdge] EdgeDetector stopped. Poll count={_pollCount}");
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private void PollTick(object? sender, EventArgs e)
    {
        try
        {
            if (!IsEnabled) return;

            _pollCount++;
            // Log every 200 polls (~10 sec) to confirm it's alive
            if (_pollCount % 200 == 1)
                Console.WriteLine($"[MusicEdge] Poll #{_pollCount}");

            if (!GetCursorPos(out POINT pt)) return;

            // Check if mouse is near right edge hot zone
            bool nearEdge = pt.X >= _screenWidth - EdgeHotZoneWidth &&
                            pt.Y >= 0 && pt.Y <= _screenHeight;

            // Check if mouse is over the panel itself
            bool overPanel = pt.X >= PanelX &&
                             pt.X <= PanelX + PanelWidth &&
                             pt.Y >= PanelY &&
                             pt.Y <= PanelY + PanelHeight;

            if (nearEdge && !_isNearEdge)
            {
                Console.WriteLine($"[MusicEdge] Near edge! X={pt.X}, screenW={_screenWidth}, zone={EdgeHotZoneWidth}");
                _isNearEdge = true;
                _lastEdgeTime = DateTime.UtcNow;
                MouseNearEdge?.Invoke();
            }
            else if (!nearEdge && !overPanel && _isNearEdge)
            {
                _isNearEdge = false;
                _lastLeaveTime = DateTime.UtcNow;
                MouseLeftEdge?.Invoke();
            }

            if (overPanel != _isOverPanel)
            {
                _isOverPanel = overPanel;
                MouseOverPanelChanged?.Invoke(overPanel);
                if (overPanel)
                    _lastEdgeTime = DateTime.UtcNow;
                else
                    _lastLeaveTime = DateTime.UtcNow;
            }

            // Hover timer logic
            if (_isNearEdge && _hoverPending)
            {
                if ((DateTime.UtcNow - _hoverTriggerTime).TotalMilliseconds >= HoverDelayMs)
                {
                    _hoverPending = false;
                    OnHoverLongEnough?.Invoke();
                }
            }

            // Hide timer logic
            if (!_isOverPanel && !_isNearEdge)
            {
                var elapsed = (DateTime.UtcNow - _lastLeaveTime).TotalMilliseconds;
                if (elapsed >= FullHideDelayMs)
                    OnShouldFullyHide?.Invoke();
                else if (elapsed >= HideDelayMs)
                    OnShouldCollapse?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MusicEdge] ERROR in PollTick: {ex}");
        }
    }

    public void NotifyPreviewShown()
    {
        _hoverPending = true;
        _hoverTriggerTime = DateTime.UtcNow;
    }

    public static bool IsForegroundFullScreen()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            GetWindowRect(hwnd, out RECT appRect);

            IntPtr hMonitor = MonitorFromWindow(hwnd, 0);
            var monitorInfo = new MONITORINFO();
            monitorInfo.cbSize = Marshal.SizeOf(monitorInfo);
            if (!GetMonitorInfo(hMonitor, ref monitorInfo))
                return false;

            var screenRect = monitorInfo.rcMonitor;
            return appRect.Left <= screenRect.Left &&
                   appRect.Top <= screenRect.Top &&
                   appRect.Right >= screenRect.Right &&
                   appRect.Bottom >= screenRect.Bottom;
        }
        catch
        {
            return false;
        }
    }

    public static uint GetIdleSeconds()
    {
        var lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(lii);
        if (GetLastInputInfo(ref lii))
        {
            return (uint)Environment.TickCount - lii.dwTime;
        }
        return 0;
    }

    public void Dispose()
    {
        Stop();
    }
}
