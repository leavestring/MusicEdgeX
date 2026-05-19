using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MusicEdge.Helpers;
using MusicEdge.Models;
using MusicEdge.Services;
using static MusicEdge.Helpers.Win32Interop;

namespace MusicEdge;

/// <summary>
/// Main music control panel window. Uses Opacity-based show/hide instead of Window.Hide/Show.
/// Window stays inside the screen bounds at all times for WPF rendering compatibility.
/// </summary>
public partial class MainWindow : Window
{
    private readonly SMTCMusicService _smtcService;
    private readonly MediaKeyService _mediaKeyService;
    private readonly QQMusicHotkeyService _qqMusicService;
    private readonly EdgeDetector _edgeDetector;

    private double _originalScreenWidth;
    private bool _isPinned;
    private bool _animating;
    private DateTime _panelShownTime = DateTime.MinValue;

    private enum PanelState { Hidden, Preview, Expanded }
    private PanelState _state = PanelState.Hidden;

    private const double PanelFullWidth = 360;
    private const double PanelPreviewWidth = 48;
    private const double EdgeHotZoneDip = 24;

    // DPI scale factors: multiply WPF DIP → physical pixels
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    // Rendering-based animation: syncs to display refresh rate (120Hz/144Hz capable)
    private double _animStartX;
    private double _animTargetX;
    private DateTime _animStartTime;
    private int _animDurationMs;
    private Action? _animOnComplete;
    private bool _animFrameHooked;

    public MainWindow()
    {
        InitializeComponent();

        _smtcService = new SMTCMusicService();
        _mediaKeyService = new MediaKeyService();
        _qqMusicService = new QQMusicHotkeyService();
        _edgeDetector = new EdgeDetector();

        _smtcService.TrackChanged += OnTrackChanged;
        _smtcService.PlaybackStateChanged += OnPlaybackStateChanged;
        _edgeDetector.MouseNearEdge += OnMouseNearEdge;
        _edgeDetector.MouseLeftEdge += OnMouseLeftEdge;
        _edgeDetector.MouseOverPanelChanged += OnMouseOverPanelChanged;
        _edgeDetector.OnHoverLongEnough += OnHoverLongEnough;
        _edgeDetector.OnShouldCollapse += OnShouldCollapse;
        _edgeDetector.OnShouldFullyHide += OnShouldFullyHide;

        this.Loaded += MainWindow_Loaded;
        this.Closing += MainWindow_Closing;
        this.Deactivated += MainWindow_Deactivated;
    }

    #region Window Lifecycle

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Capture DPI scale after HWND exists (ApplyBackdropEffect ensures this)
        var source = System.Windows.PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            _dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
            _dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        // Convert physical pixel screen size to WPF device-independent pixels
        _originalScreenWidth = GetSystemMetrics(0) / _dpiScaleX;
        double screenHeight = GetSystemMetrics(1) / _dpiScaleY;

        // Position at screen right edge (fully off-screen to the right)
        this.Left = _originalScreenWidth;
        this.Top = (screenHeight - this.Height) / 2;
        this.Width = PanelFullWidth;
        this.Height = 490;
        this.Opacity = 0.96;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
            // Tell Windows the frame style changed so the new styles take effect
            SetWindowPos(hwnd, 0, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        ApplyBackdropEffect();
        UpdateEdgeDetectorGeometry();
        _edgeDetector.Start();
        _smtcService.Start();

        // Start invisible — positioned off-screen at screen right edge
        this.Opacity = 0;

        Console.WriteLine($"[MusicEdge] Loaded. DPI={_dpiScaleX:F2}x{_dpiScaleY:F2}, screenDIP={_originalScreenWidth:F0}x{screenHeight:F0}, Left={this.Left:F0}");
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _edgeDetector.Stop();
        _smtcService.Stop();
        _edgeDetector.Dispose();
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        if (!_isPinned && _state == PanelState.Expanded)
            this.Topmost = true;
    }

    private void ApplyBackdropEffect()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            if (hwnd == IntPtr.Zero) return;

            // NOTE: DWMWA_SYSTEMBACKDROP_TYPE (Mica/Acrylic) is NOT compatible
            // with AllowsTransparency="True" (layered windows). The semi-transparent
            // Border background (#E61A1A2E) already provides the desired glassy look,
            // so we skip the backdrop attribute and only apply dark mode.

            int useDarkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref useDarkMode, sizeof(int));

            Console.WriteLine("[MusicEdge] Backdrop effect skipped (incompatible with layered window), dark mode applied");
        }
        catch { }
    }

    private void UpdateEdgeDetectorGeometry()
    {
        // Convert WPF DIP positions to physical pixels for EdgeDetector
        // (EdgeDetector uses GetCursorPos which returns physical pixels)
        _edgeDetector.PanelX = this.Left * _dpiScaleX;
        _edgeDetector.PanelY = this.Top * _dpiScaleY;
        _edgeDetector.PanelWidth = this.Width * _dpiScaleX;
        _edgeDetector.PanelHeight = this.Height * _dpiScaleY;
        _edgeDetector.PanelVisibleX = (_originalScreenWidth - PanelFullWidth) * _dpiScaleX;
        _edgeDetector.EdgeHotZoneWidth = (int)(EdgeHotZoneDip * _dpiScaleX);
        _edgeDetector.PreviewPanelWidth = (int)(PanelPreviewWidth * _dpiScaleX);
        _edgeDetector.FullPanelWidth = (int)(PanelFullWidth * _dpiScaleX);
    }

    #endregion

    #region Edge Detection Handlers

    private void OnMouseNearEdge()
    {
        Console.WriteLine($"[MusicEdge] OnMouseNearEdge: state={_state}, animating={_animating}");

        if (_state == PanelState.Hidden && !_animating)
        {
            if (EdgeDetector.IsForegroundFullScreen())
            {
                Console.WriteLine("[MusicEdge] Fullscreen detected, skipping");
                return;
            }
            ShowPreview();
        }
        else
        {
            Console.WriteLine($"[MusicEdge] OnMouseNearEdge skipped (state={_state}, animating={_animating})");
        }
    }

    private void OnMouseLeftEdge()
    {
        // Handled by timers in EdgeDetector
    }

    private void OnMouseOverPanelChanged(bool isOver)
    {
        if (isOver && _state == PanelState.Preview)
            _edgeDetector.NotifyPreviewShown();
    }

    private void OnHoverLongEnough()
    {
        if (_state == PanelState.Preview && !_animating)
            ShowExpanded();
    }

    private void OnShouldCollapse()
    {
        // Don't collapse within 1.5s of showing (grace period for mouse to settle)
        if ((DateTime.UtcNow - _panelShownTime).TotalMilliseconds < 1500) return;
        if (_state == PanelState.Expanded && !_isPinned && !_animating)
            ShowPreview();
    }

    private void OnShouldFullyHide()
    {
        // Don't hide within 1.5s of showing (grace period for mouse to settle)
        if ((DateTime.UtcNow - _panelShownTime).TotalMilliseconds < 1500) return;
        if (_state == PanelState.Preview && !_isPinned && !_animating)
            HidePanel();
    }

    #endregion

    #region Panel Show/Hide (Opacity-based)

    private void ShowPreview()
    {
        if (_animating)
        {
            Console.WriteLine("[MusicEdge] ShowPreview blocked (animating)");
            return;
        }
        _animating = true;
        _state = PanelState.Preview;
        _panelShownTime = DateTime.UtcNow;
        Console.WriteLine("[MusicEdge] ShowPreview: fading in + sliding to preview position");

        // Fade in
        this.Opacity = 0.96;

        var targetX = _originalScreenWidth - PanelPreviewWidth;
        AnimateToX(targetX, 200, () =>
        {
            _animating = false;
            UpdateEdgeDetectorGeometry();
            Console.WriteLine($"[MusicEdge] ShowPreview done. Left={this.Left}, Opacity={this.Opacity}");
        });
    }

    private void ShowExpanded()
    {
        if (_animating)
        {
            Console.WriteLine("[MusicEdge] ShowExpanded blocked (animating)");
            return;
        }
        _animating = true;
        _state = PanelState.Expanded;
        _panelShownTime = DateTime.UtcNow;
        Console.WriteLine("[MusicEdge] ShowExpanded: sliding to full width");

        this.Opacity = 0.96;
        var targetX = _originalScreenWidth - PanelFullWidth;
        AnimateToX(targetX, 350, () =>
        {
            _animating = false;
            _edgeDetector.NotifyPreviewShown();
            UpdateEdgeDetectorGeometry();
            Console.WriteLine($"[MusicEdge] ShowExpanded done. Left={this.Left}");
        });
    }

    private void HidePanel()
    {
        if (_animating)
        {
            Console.WriteLine("[MusicEdge] HidePanel blocked (animating)");
            return;
        }
        _animating = true;
        _state = PanelState.Hidden;
        Console.WriteLine("[MusicEdge] HidePanel: sliding to screen edge + fading out");

        var targetX = _originalScreenWidth;
        AnimateToX(targetX, 200, () =>
        {
            _animating = false;
            this.Opacity = 0;
            UpdateEdgeDetectorGeometry();
            Console.WriteLine("[MusicEdge] HidePanel done (opacity=0)");
        });
    }

    /// <summary>
    /// Frame-based window position animation synced to display refresh rate
    /// via CompositionTarget.Rendering. Works up to 120Hz/144Hz.
    /// </summary>
    private void AnimateToX(double targetX, int durationMs, Action? onComplete = null)
    {
        _animOnComplete = null;
        _animStartX = this.Left;
        _animTargetX = targetX;
        _animDurationMs = durationMs;
        _animStartTime = DateTime.UtcNow;
        _animOnComplete = onComplete;

        if (!_animFrameHooked)
        {
            _animFrameHooked = true;
            CompositionTarget.Rendering += OnAnimFrame;
        }
    }

    private void OnAnimFrame(object? sender, EventArgs e)
    {
        double elapsed = (DateTime.UtcNow - _animStartTime).TotalMilliseconds;
        double progress = Math.Min(elapsed / _animDurationMs, 1.0);

        // Ease-out cubic: smooth natural deceleration
        double t = 1.0 - progress;
        double eased = 1.0 - (t * t * t);

        this.Left = _animStartX + (_animTargetX - _animStartX) * eased;
        UpdateEdgeDetectorGeometry();

        if (progress >= 1.0)
        {
            CompositionTarget.Rendering -= OnAnimFrame;
            _animFrameHooked = false;
            this.Left = _animTargetX;
            UpdateEdgeDetectorGeometry();

            var cb = _animOnComplete;
            _animOnComplete = null;
            cb?.Invoke();
        }
    }

    #endregion

    #region Track Info Handlers

    private void OnTrackChanged(TrackInfo track)
    {
        Dispatcher.Invoke(() => UpdateTrackUI(track));
    }

    private void OnPlaybackStateChanged(PlaybackState state)
    {
        Dispatcher.Invoke(() => UpdatePlayPauseButton(state));
    }

    private string _lastAlbumArtHash = "";

    private void UpdateTrackUI(TrackInfo track)
    {
        if (track.IsEmpty || track.State == PlaybackState.Closed)
        {
            TxtSongTitle.Text = "QQ音乐未运行";
            TxtArtist.Text = "打开 QQ 音乐开始播放";
            TxtCurrentTime.Text = "0:00";
            TxtTotalTime.Text = "0:00";
            ProgressBar.Value = 0;
            AnimateAlbumArtOut();
            BtnPlayPauseIcon.Data = (Geometry)FindResource("GeoPlay");
            return;
        }

        TxtSongTitle.Text = track.Title;
        TxtArtist.Text = track.Artist;

        if (track.Duration.TotalSeconds > 0)
        {
            TxtCurrentTime.Text = FormatTime(track.Position);
            TxtTotalTime.Text = FormatTime(track.Duration);
            ProgressBar.Maximum = track.Duration.TotalSeconds;
            ProgressBar.Value = track.Position.TotalSeconds;
        }

        if (track.ThumbnailBytes != null && track.ThumbnailBytes.Length > 0)
        {
            // Simple hash to detect if the album art actually changed
            var hash = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(track.ThumbnailBytes));
            if (hash != _lastAlbumArtHash)
            {
                _lastAlbumArtHash = hash;
                try
                {
                    using var ms = new MemoryStream(track.ThumbnailBytes);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                    AlbumArtImage.Source = bitmap;
                    AnimateAlbumArtIn();
                }
                catch
                {
                    AnimateAlbumArtOut();
                }
            }
        }
        else
        {
            _lastAlbumArtHash = "";
            AnimateAlbumArtOut();
        }

        UpdatePlayPauseButton(track.State);
    }

    private void AnimateAlbumArtIn()
    {
        AlbumArtPlaceholder.Visibility = Visibility.Collapsed;
        AlbumArtImage.Visibility = Visibility.Visible;
        AlbumArtImage.Opacity = 0;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = EasingMode.EaseOut }
        };
        AlbumArtImage.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    private void AnimateAlbumArtOut()
    {
        _lastAlbumArtHash = "";
        var fadeOut = new DoubleAnimation(AlbumArtImage.Opacity, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fadeOut.Completed += (s, e) =>
        {
            AlbumArtImage.Visibility = Visibility.Collapsed;
            AlbumArtPlaceholder.Visibility = Visibility.Visible;
        };
        AlbumArtImage.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void UpdatePlayPauseButton(PlaybackState state)
    {
        BtnPlayPauseIcon.Data = state == PlaybackState.Playing
            ? (Geometry)FindResource("GeoPause")
            : (Geometry)FindResource("GeoPlay");
    }

    private static string FormatTime(TimeSpan ts)
    {
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }

    #endregion

    #region Public Show/Hide (called from tray icon)

    public void ShowPanelFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            var source = System.Windows.PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                _dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
                _dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
            }
            _originalScreenWidth = GetSystemMetrics(0) / _dpiScaleX;
            UpdateEdgeDetectorGeometry();

            CancelAnimation();
            _isPinned = true;
            BtnPin.Content = "📌";

            this.Opacity = 0.96;
            this.Left = _originalScreenWidth - PanelFullWidth;
            _state = PanelState.Expanded;
            _panelShownTime = DateTime.UtcNow;
            UpdateEdgeDetectorGeometry();
            _edgeDetector.NotifyPreviewShown();
            Console.WriteLine($"[MusicEdge] ShowPanelFromTray: Left={this.Left}, Opacity={this.Opacity}");
        });
    }

    public void HidePanelFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            CancelAnimation();
            _isPinned = false;
            BtnPin.Content = "📍";
            _state = PanelState.Hidden;
            this.Left = _originalScreenWidth;
            this.Opacity = 0;
            UpdateEdgeDetectorGeometry();
            Console.WriteLine("[MusicEdge] HidePanelFromTray");
        });
    }

    private void CancelAnimation()
    {
        if (_animFrameHooked)
        {
            CompositionTarget.Rendering -= OnAnimFrame;
            _animFrameHooked = false;
        }
        _animOnComplete = null;
        _animating = false;
    }

    #endregion

    #region Control Button Handlers

    private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
    {
        _mediaKeyService.TogglePlayPause();
        AnimateButtonPress(BtnPlayPause);
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e) => _mediaKeyService.NextTrack();
    private void BtnPrevious_Click(object sender, RoutedEventArgs e) => _mediaKeyService.PreviousTrack();

    private void BtnShuffle_Click(object sender, RoutedEventArgs e)
    {
        AnimateButtonPress(sender as Button);
    }

    private bool _isLiked;

    private void BtnLike_Click(object sender, RoutedEventArgs e)
    {
        _qqMusicService.ToggleLike();
        _isLiked = !_isLiked;

        var accentHot = new SolidColorBrush(Color.FromRgb(0xE9, 0x45, 0x60));
        var textB = new SolidColorBrush(Color.FromRgb(0x92, 0x92, 0xAA));

        if (_isLiked)
        {
            BtnLikeIcon.Fill = accentHot;
            BtnLikeIcon.Stroke = accentHot;
        }
        else
        {
            BtnLikeIcon.Fill = Brushes.Transparent;
            BtnLikeIcon.Stroke = textB;
        }

        ShowToast(_isLiked ? "已添加到我爱听" : "已取消收藏");
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Volume display only for now
    }

    private void BtnPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (QQMusicHotkeyService.IsQQMusicRunning())
            QQMusicHotkeyService.BringQQMusicToFront();
        else
            QQMusicHotkeyService.LaunchQQMusic();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_edgeDetector);
        settingsWindow.Owner = this;
        settingsWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        settingsWindow.ShowDialog();
    }

    #endregion

    #region Title Bar Handlers

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        BtnPin.Content = _isPinned ? "📌" : "📍";
        ShowToast(_isPinned ? "面板已固定" : "面板将自动隐藏");
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = false;
        BtnPin.Content = "📍";
        HidePanel();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            this.DragMove();
            UpdateEdgeDetectorGeometry();
        }
    }

    #endregion

    #region Visual Feedback

    private void AnimateButtonPress(Button? button)
    {
        if (button == null) return;

        var scaleTransform = new ScaleTransform(1, 1);
        button.RenderTransform = scaleTransform;
        button.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = EasingMode.EaseInOut };

        var storyboard = new Storyboard();
        var scaleX = new DoubleAnimation
        {
            From = 1.0, To = 0.88,
            Duration = TimeSpan.FromMilliseconds(70),
            AutoReverse = true,
            EasingFunction = ease
        };
        Storyboard.SetTarget(scaleX, button);
        Storyboard.SetTargetProperty(scaleX, new PropertyPath("RenderTransform.ScaleX"));
        storyboard.Children.Add(scaleX);

        var scaleY = new DoubleAnimation
        {
            From = 1.0, To = 0.88,
            Duration = TimeSpan.FromMilliseconds(70),
            AutoReverse = true,
            EasingFunction = ease
        };
        Storyboard.SetTarget(scaleY, button);
        Storyboard.SetTargetProperty(scaleY, new PropertyPath("RenderTransform.ScaleY"));
        storyboard.Children.Add(scaleY);

        storyboard.Begin();
    }

    private string _toastPendingArtist = "";

    private void ShowToast(string message)
    {
        Dispatcher.Invoke(() =>
        {
            _toastPendingArtist = TxtArtist.Text;
            TxtArtist.Text = message;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                TxtArtist.Text = _toastPendingArtist;
            };
            timer.Start();
        });
    }

    #endregion
}
