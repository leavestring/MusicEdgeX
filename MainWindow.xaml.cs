using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MusicEdge.Models;
using MusicEdge.Services;
using static MusicEdge.Helpers.Win32Interop;

namespace MusicEdge;

public partial class MainWindow : Window
{
    private const double PanelWidth = 372;
    private const double PanelHeight = 512;
    private const double PreviewWidth = 56;
    private const int PreviewDurationMs = 180;
    private const int ExpandDurationMs = 320;
    private const int CollapseDurationMs = 220;
    private const int HideDurationMs = 180;
    private const int ShowGraceMs = 850;

    private readonly MusicSourceManager _music;
    private readonly MediaKeyService _mediaKeys;
    private readonly QQMusicHotkeyService _qqMusic;
    private readonly EdgeDetector _edge;
    private readonly SystemVolumeService _volume;

    private AppSettings _settings;
    private readonly bool _showOnStartup;
    private IntPtr _hwnd;
    private double _dpiX = 1.0;
    private double _dpiY = 1.0;

    private enum PanelState { Hidden, Preview, Expanded }
    private PanelState _state = PanelState.Hidden;
    private bool _isPinned;
    private bool _animating;
    private DateTime _panelShownTime = DateTime.MinValue;

    private readonly Stopwatch _animClock = new();
    private int _animFromX;
    private int _animToX;
    private int _animDuration;
    private Action? _animOnComplete;
    private bool _animFrameHooked;

    private TrackInfo _track = new();
    private bool _isPlaying;
    private bool _shuffleActive;
    private bool _supportsShuffle;
    private bool _isSeeking;
    private bool _isMuted;
    private bool _suppressVolume;
    private ulong _lastArtHash;
    private long _artVersion;
    private IReadOnlyList<LyricLine> _lyrics = Array.Empty<LyricLine>();
    private int _activeLyricIndex = -1;
    private string _lastRailLyric = string.Empty;
    private string _lastRailLyricText = string.Empty;
    private bool _lastRailLyricHighlight;

    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _fullscreenTimer;

    private static readonly Color DefaultAccentA = Color.FromRgb(0xC9, 0x60, 0x4A);
    private static readonly Color DefaultAccentB = Color.FromRgb(0xA8, 0x73, 0x4F);
    private static readonly Color DefaultAccentC = Color.FromRgb(0x7D, 0x4B, 0x42);

    private const string PlayIconData = "M8,5v14l11,-7z";
    private const string PauseIconData = "M6,19h4V5H6v14zm8,-14v14h4V5h-4z";
    private const string VolumeIconData =
        "M3,9v6h4l5,5V4L7,9H3z M16.5,12c0,-1.77 -1.02,-3.29 -2.5,-4.03v8.05c1.48,-0.73 2.5,-2.25 2.5,-4.02zM14,3.23v2.06c2.89,0.86 5,3.54 5,6.71s-2.11,5.85 -5,6.71v2.06c4.01,-0.91 7,-4.49 7,-8.77s-2.99,-7.86 -7,-8.77z";
    private const string VolumeMutedIconData =
        "M3,9v6h4l5,5V4L7,9H3z M16.5,12l3.5,-3.5 -1.41,-1.41 -3.5,3.5 -3.5,-3.5 -1.41,1.41 3.5,3.5 -3.5,3.5 1.41,1.41 3.5,-3.5 3.5,3.5 1.41,-1.41z";

    public MainWindow(AppSettings settings, bool showOnStartup = false)
    {
        _settings = settings;
        _showOnStartup = showOnStartup;

        _music = new MusicSourceManager(settings);
        _mediaKeys = new MediaKeyService();
        _qqMusic = new QQMusicHotkeyService();
        _edge = new EdgeDetector();
        _volume = new SystemVolumeService();

        InitializeComponent();

        _music.TrackChanged += OnTrackChanged;
        _music.PlaybackStateChanged += OnPlaybackStateChanged;
        _music.PositionChanged += OnPositionChanged;
        _music.ShuffleChanged += OnShuffleChanged;
        _music.SourceStatusChanged += OnSourceStatusChanged;

        _edge.MouseNearEdge += OnMouseNearEdge;
        _edge.OnHoverLongEnough += OnHoverLongEnough;
        _edge.OnShouldCollapse += OnShouldCollapse;
        _edge.OnShouldFullyHide += OnShouldFullyHide;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        DpiChanged += OnDpiChanged;
    }

    #region Window setup

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        try
        {
            int dark = 1;
            DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch { }

        UpdateDpi();
        ApplyEdgeSideLayout();
        _edge.PanelHandle = _hwnd;
        SnapToState(PanelState.Hidden);
        Opacity = 0;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateDpi();
        ApplySettings();
        SnapToState(PanelState.Hidden);
        Opacity = 0;

        _music.Start();
        _edge.Start();
        RefreshVolume();

        if (_settings.StartPinned || _showOnStartup)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                ShowPanelFromTray();
            }));
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        CancelAnimation();
        _toastTimer?.Stop();
        _fullscreenTimer?.Stop();
        _music.Dispose();
        _volume.Dispose();
        _edge.Dispose();
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            UpdateDpi();
            if (_state == PanelState.Hidden)
                SnapToState(PanelState.Hidden);
            else
                SnapToState(_state);
        }));
    }

    private void UpdateDpi()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiX = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
        _dpiY = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;
    }

    private RECT GetWorkArea()
    {
        IntPtr monitor = _hwnd != IntPtr.Zero
            ? MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST)
            : MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info) ? info.rcWork : new RECT { Right = 1920, Bottom = 1080 };
    }

    private int GetTargetX(PanelState state)
    {
        var work = GetWorkArea();
        int panel = (int)Math.Round(PanelWidth * _dpiX);
        int preview = (int)Math.Round(PreviewWidth * _dpiX);
        bool leftEdge = _settings.EdgeSide == "Left";

        if (!leftEdge)
        {
            return state switch
            {
                PanelState.Expanded => work.Right - panel,
                PanelState.Preview => work.Right - preview,
                _ => work.Right
            };
        }

        return state switch
        {
            PanelState.Expanded => work.Left,
            PanelState.Preview => work.Left - panel + preview,
            _ => work.Left - panel
        };
    }

    private void SetPanelX(int x)
    {
        if (_hwnd == IntPtr.Zero) return;

        var work = GetWorkArea();
        int panelHeight = (int)Math.Round(PanelHeight * _dpiY);
        int top = work.Top + Math.Max(0, (work.Height - panelHeight) / 2);

        SetWindowPos(_hwnd, IntPtr.Zero, x, top, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void SnapToState(PanelState state)
    {
        SetPanelX(GetTargetX(state));
        _state = state;
    }

    private void ApplyEdgeSideLayout()
    {
        bool left = _settings.EdgeSide == "Left";

        Grid.SetColumn(Rail, left ? 1 : 0);
        Grid.SetColumn(ContentPanel, left ? 0 : 1);

        ColRail.Width = left ? new GridLength(1, GridUnitType.Star) : new GridLength(PreviewWidth);
        ColContent.Width = left ? new GridLength(PreviewWidth) : new GridLength(1, GridUnitType.Star);

        var radius = left
            ? new CornerRadius(0, 16, 16, 0)
            : new CornerRadius(16, 0, 0, 16);

        PanelBorder.CornerRadius = radius;
        Rail.CornerRadius = radius;
        PanelBorder.BorderThickness = left
            ? new Thickness(0, 1, 1, 1)
            : new Thickness(1, 1, 0, 1);
    }

    private void ApplySettings()
    {
        _edge.HoverDelayMs = _settings.HoverDelayMs;
        _edge.CollapseDelayMs = _settings.CollapseDelayMs;
        _edge.HideDelayMs = _settings.HideDelayMs;
        _edge.EdgeSide = _settings.EdgeSide;
        _edge.IsEnabled = true;

        _music.UpdateSettings(_settings);
        ApplyEdgeSideLayout();

        if (_state == PanelState.Hidden)
            SnapToState(PanelState.Hidden);
        else
            SnapToState(_state);

        if (_settings.DisableInFullscreen && _state != PanelState.Hidden)
            StartFullscreenWatch();
        else
            StopFullscreenWatch();
    }

    #endregion

    #region Panel animation

    private void AnimateTo(PanelState target, int durationMs, Action? onComplete = null)
    {
        CancelAnimation();

        _animFrameHooked = true;
        _animating = true;
        _animFromX = GetWindowX();
        _animToX = GetTargetX(target);
        _animDuration = Math.Max(1, durationMs);
        _animOnComplete = onComplete;
        _animClock.Restart();
        CompositionTarget.Rendering += OnAnimationFrame;
    }

    private int GetWindowX()
    {
        if (_hwnd == IntPtr.Zero) return GetTargetX(_state);
        return GetWindowRect(_hwnd, out RECT rect) ? rect.Left : GetTargetX(_state);
    }

    private void OnAnimationFrame(object? sender, EventArgs e)
    {
        double progress = Math.Clamp(_animClock.Elapsed.TotalMilliseconds / _animDuration, 0, 1);
        double t = 1 - progress;
        double eased = 1 - t * t * t;
        int x = (int)Math.Round(_animFromX + (_animToX - _animFromX) * eased);
        SetPanelX(x);

        if (progress < 1) return;

        FinishAnimation();
    }

    private void FinishAnimation()
    {
        if (!_animFrameHooked) return;

        CompositionTarget.Rendering -= OnAnimationFrame;
        _animFrameHooked = false;
        _animating = false;
        SetPanelX(_animToX);

        var callback = _animOnComplete;
        _animOnComplete = null;
        callback?.Invoke();
    }

    private void CancelAnimation()
    {
        if (_animFrameHooked)
        {
            CompositionTarget.Rendering -= OnAnimationFrame;
            _animFrameHooked = false;
        }

        _animating = false;
        _animOnComplete = null;
    }

    private void ShowPreview()
    {
        if (_state != PanelState.Hidden || _animating) return;
        if (_settings.DisableInFullscreen && EdgeDetector.IsForegroundFullScreen(_hwnd)) return;

        _state = PanelState.Preview;
        _panelShownTime = DateTime.UtcNow;
        Opacity = 1;
        _music.IsPanelVisible = true;
        StartFullscreenWatch();

        AnimateTo(PanelState.Preview, PreviewDurationMs, () =>
        {
            if (_state == PanelState.Preview)
            {
                _edge.NotifyPreviewShown();
                UpdateRailPlayingDot();
            }
        });
    }

    private void ShowExpanded()
    {
        if (_state == PanelState.Expanded && !_animating) return;

        _state = PanelState.Expanded;
        _panelShownTime = DateTime.UtcNow;
        Opacity = 1;
        _music.IsPanelVisible = true;
        _edge.NotifyPanelExpanded();
        StartFullscreenWatch();

        AnimateTo(PanelState.Expanded, ExpandDurationMs, () =>
        {
            if (_state == PanelState.Expanded)
            {
                _edge.NotifyPanelExpanded();
                UpdateRailPlayingDot();
            }
        });
    }

    private void CollapseToPreview()
    {
        if (_state != PanelState.Expanded || _animating) return;

        _state = PanelState.Preview;
        AnimateTo(PanelState.Preview, CollapseDurationMs, () =>
        {
            if (_state == PanelState.Preview)
            {
                _edge.NotifyPreviewCollapsed();
                UpdateRailPlayingDot();
            }
        });
    }

    private void HidePanel()
    {
        if (_state == PanelState.Hidden && !_animating) return;

        _state = PanelState.Hidden;
        AnimateTo(PanelState.Hidden, HideDurationMs, () =>
        {
            if (_state != PanelState.Hidden) return;
            Opacity = 0;
            _music.IsPanelVisible = false;
            _edge.NotifyPanelHidden();
            StopFullscreenWatch();
        });
    }

    private void StartFullscreenWatch()
    {
        if (!_settings.DisableInFullscreen) return;

        _fullscreenTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };

        if (_fullscreenTimer.IsEnabled) return;

        _fullscreenTimer.Tick += OnFullscreenTick;
        _fullscreenTimer.Start();
    }

    private void StopFullscreenWatch()
    {
        if (_fullscreenTimer == null) return;
        _fullscreenTimer.Stop();
        _fullscreenTimer.Tick -= OnFullscreenTick;
    }

    private void OnFullscreenTick(object? sender, EventArgs e)
    {
        if (_state == PanelState.Hidden || _isPinned) return;
        if (EdgeDetector.IsForegroundFullScreen(_hwnd))
            HidePanel();
    }

    #endregion

    #region Edge detector handlers

    private void OnMouseNearEdge()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnMouseNearEdge));
            return;
        }
        ShowPreview();
    }

    private void OnHoverLongEnough()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnHoverLongEnough));
            return;
        }
        if (_state == PanelState.Preview && !_animating)
            ShowExpanded();
    }

    private void OnShouldCollapse()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnShouldCollapse));
            return;
        }

        if ((DateTime.UtcNow - _panelShownTime).TotalMilliseconds < ShowGraceMs) return;
        if (_isPinned || _animating) return;
        if (_state == PanelState.Expanded)
            CollapseToPreview();
    }

    private void OnShouldFullyHide()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnShouldFullyHide));
            return;
        }

        if ((DateTime.UtcNow - _panelShownTime).TotalMilliseconds < ShowGraceMs) return;
        if (_isPinned || _animating) return;
        if (_state == PanelState.Preview)
            HidePanel();
    }

    #endregion

    #region SMTC handlers

    private void OnTrackChanged(TrackInfo track)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => UpdateTrack(track)));
    }

    private void OnPlaybackStateChanged(PlaybackState state)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => UpdatePlaybackState(state)));
    }

    private void OnPositionChanged(TimeSpan position)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => UpdatePosition(position)));
    }

    private void OnShuffleChanged(bool active)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => SetShuffleVisual(active)));
    }

    private void OnSourceStatusChanged(PlayerCapConnection connection, string detail)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_music.IsPlayerCapActive) return;
            TxtSourceKind.Text = connection == PlayerCapConnection.Connected
                ? "PLAYERCAP READY"
                : "SMTC";
            SourceDot.Opacity = connection == PlayerCapConnection.Connected ? 1 : 0.35;
        }));
    }

    private async void UpdateTrack(TrackInfo track)
    {
        _track = track;

        if (track.IsEmpty || track.State == PlaybackState.Closed)
        {
            TxtSongTitle.Text = "未在播放";
            TxtArtist.Text = "打开音乐播放器开始播放";
            TxtSource.Text = "未连接";
            TxtSourceKind.Text = "SMTC";
            TxtAlbum.Text = string.Empty;
            TxtElapsed.Text = "0:00";
            TxtTotal.Text = "0:00";
            SliderSeek.Maximum = 1;
            SliderSeek.Value = 0;
            SliderSeek.IsEnabled = false;
            BtnLike.IsEnabled = false;
            BtnShuffle.IsEnabled = false;
            SourceDot.Opacity = 0.35;
            SetLyricTexts(string.Empty, "让音乐留在边缘",
                "QQ音乐 / Spotify / 网易云 / Apple Music", string.Empty);
            LyricMarker.Opacity = 0;
            UpdateRailLyric("MUSIC", highlight: false);
            SetShuffleVisual(false);
            await ClearArtworkAsync();
            UpdatePlaybackState(PlaybackState.Closed);
            return;
        }

        TxtSongTitle.Text = string.IsNullOrWhiteSpace(track.Title) ? "未知曲目" : track.Title;
        TxtArtist.Text = string.IsNullOrWhiteSpace(track.Artist)
            ? (string.IsNullOrWhiteSpace(track.Album) ? "未知艺术家" : track.Album)
            : track.Artist;
        TxtAlbum.Text = track.Album;
        TxtSource.Text = track.SourceKind == "PlayerCap"
            ? $"{track.SourceName} · 桥接歌词"
            : track.SourceName;
        TxtSourceKind.Text = track.SourceKind == "PlayerCap" ? "PLAYERCAP" : "SMTC";
        BtnOpenPlayer.ToolTip = track.IsQQMusic ? "打开 QQ 音乐" : "打开播放器";
        BtnLike.IsEnabled = track.IsQQMusic;
        BtnShuffle.IsEnabled = track.SupportsShuffle;
        _supportsShuffle = track.SupportsShuffle;
        SetShuffleVisual(track.IsShuffleActive);
        SourceDot.Opacity = 1;

        double duration = Math.Max(0, track.Duration.TotalSeconds);
        SliderSeek.IsEnabled = duration > 1;
        SliderSeek.Maximum = duration > 1 ? duration : 1;
        TxtTotal.Text = FormatTime(track.Duration);

        if (!_isSeeking)
        {
            SliderSeek.Value = Math.Clamp(track.Position.TotalSeconds, 0, SliderSeek.Maximum);
            TxtElapsed.Text = FormatTime(track.Position);
        }

        UpdatePlaybackState(track.State);
        UpdateLyricSource(track);

        if (track.ThumbnailBytes is { Length: > 0 })
        {
            ulong hash = QuickHash(track.ThumbnailBytes);
            if (hash == _lastArtHash) return;
            _lastArtHash = hash;

            long version = ++_artVersion;
            var result = DecodeArtwork(track.ThumbnailBytes);
            if (version != _artVersion) return;

            if (result == null)
            {
                await ClearArtworkAsync();
                return;
            }

            AlbumArtBrush.ImageSource = result.Image;
            MiniArtBrush.ImageSource = result.Image;
            AlbumArtLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
            MiniArtLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
            ApplyAccent(result.AccentA, result.AccentB, result.AccentC);
        }
        else if (_lastArtHash != 0)
        {
            await ClearArtworkAsync();
        }
    }

    private void UpdateLyricSource(TrackInfo track)
    {
        _lyrics = track.Lyrics ?? Array.Empty<LyricLine>();
        _activeLyricIndex = -1;

        if (_lyrics.Count == 0)
        {
            string current;
            string hint;

            if (track.SourceKind == "PlayerCap")
            {
                current = "纯音乐，请欣赏";
                hint = string.IsNullOrWhiteSpace(track.Album) ? "当前曲目没有歌词数据" : track.Album;
            }
            else
            {
                current = "正在聆听";
                hint = "启用 PlayerCap 可显示边缘歌词";
            }

            SetLyricTexts(string.Empty, current, hint, string.Empty);
            LyricMarker.Opacity = 0;
            UpdateRailLyric(string.IsNullOrWhiteSpace(track.Title) ? "MUSIC" : track.Title,
                highlight: false);
            return;
        }

        UpdateLyricForPosition(track.Position, force: true);
    }

    private void UpdateLyricForPosition(TimeSpan position, bool force = false)
    {
        if (_lyrics.Count == 0) return;

        int index = FindLyricIndex(position);
        if (!force && index == _activeLyricIndex) return;
        _activeLyricIndex = index;

        int currentIndex = index < 0 ? 0 : index;
        var current = _lyrics[currentIndex];
        var previous = currentIndex > 0 ? _lyrics[currentIndex - 1] : null;
        var next = currentIndex + 1 < _lyrics.Count ? _lyrics[currentIndex + 1] : null;

        SetLyricTexts(
            previous?.Text ?? string.Empty,
            current.Text,
            current.Translation,
            next?.Text ?? string.Empty);

        LyricMarker.Opacity = 1;
        UpdateRailLyric(current.Text, highlight: true);
        AnimateLyricChange();
    }

    private int FindLyricIndex(TimeSpan position)
    {
        int low = 0;
        int high = _lyrics.Count - 1;
        int result = -1;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (_lyrics[mid].Start <= position)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    private void SetLyricTexts(string previous, string current, string translation, string next)
    {
        TxtLyricPrev.Text = previous;
        TxtLyricCurrent.Text = current;
        TxtLyricTranslation.Text = translation;
        TxtLyricTranslation.Visibility = string.IsNullOrWhiteSpace(translation)
            ? Visibility.Collapsed
            : Visibility.Visible;
        TxtLyricNext.Text = next;
    }

    private void AnimateLyricChange()
    {
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = EasingMode.EaseOut };
        TxtLyricCurrent.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });

        if (TxtLyricCurrent.RenderTransform is TranslateTransform shift)
        {
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }
    }

    private void UpdateRailLyric(string text, bool highlight, bool force = false)
    {
        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text)) text = "MUSIC";

        var key = $"{highlight}|{text}";
        if (!force && string.Equals(key, _lastRailLyric, StringComparison.Ordinal)) return;
        _lastRailLyric = key;
        _lastRailLyricText = text;
        _lastRailLyricHighlight = highlight;

        RailLyricPanel.Children.Clear();
        var chars = text.Where(ch => !char.IsControl(ch)).Take(7).ToArray();
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.White;
        var secondary = TryFindResource("TextSecondary") as Brush ?? Brushes.Gray;

        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsWhiteSpace(chars[i])) continue;

            RailLyricPanel.Children.Add(new TextBlock
            {
                Text = chars[i].ToString(),
                FontFamily = new FontFamily("Songti SC, SimSun, Noto Serif CJK SC"),
                FontSize = 12.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 1),
                Foreground = highlight && i == 0 ? accent : secondary,
                Opacity = highlight ? 0.92 : 0.55
            });
        }

        if (chars.Length == 7 && text.Length > 7)
        {
            RailLyricPanel.Children.Add(new TextBlock
            {
                Text = "·",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = secondary,
                Opacity = 0.5
            });
        }
    }

    private void UpdatePosition(TimeSpan position)
    {
        if (!_isSeeking && !_track.IsEmpty && _track.Duration > TimeSpan.Zero)
        {
            double seconds = Math.Clamp(position.TotalSeconds, 0, SliderSeek.Maximum);
            SliderSeek.Value = seconds;
            TxtElapsed.Text = FormatTime(position);
        }

        UpdateLyricForPosition(position);
    }

    private void UpdatePlaybackState(PlaybackState state)
    {
        _isPlaying = state == PlaybackState.Playing;

        var data = _isPlaying ? PauseIconData : PlayIconData;
        PlayIcon.Data = Geometry.Parse(data);
        RailPlayIcon.Data = Geometry.Parse(data);

        RailPlayingDot.Opacity = _isPlaying ? 1 : 0;
    }

    private void UpdateRailPlayingDot()
    {
        RailPlayingDot.Opacity = _isPlaying ? 1 : 0;
    }

    private void SetShuffleVisual(bool active)
    {
        _shuffleActive = active;
        var brush = active
            ? TryFindResource("AccentBrush") as Brush ?? Brushes.White
            : TryFindResource("TextSecondary") as Brush ?? Brushes.Gray;

        BtnShuffle.Foreground = brush;
        ShuffleIcon.Fill = brush;
        BtnShuffle.ToolTip = active ? "随机播放：开" : "随机播放：关";
    }

    private void ApplyAccent(Color a, Color b, Color c)
    {
        var solid = new SolidColorBrush(a);
        solid.Freeze();

        Resources["AccentBrush"] = solid;

        SetShuffleVisual(_shuffleActive);
        UpdatePinVisual();
        UpdateRailLyric(_lastRailLyricText, _lastRailLyricHighlight, force: true);
    }

    private void ResetAccent()
    {
        ApplyAccent(DefaultAccentA, DefaultAccentB, DefaultAccentC);
    }

    private async Task ClearArtworkAsync()
    {
        _lastArtHash = 0;
        long version = ++_artVersion;
        AlbumArtLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
        MiniArtLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
        await Task.Delay(150);
        if (version != _artVersion) return;

        AlbumArtBrush.ImageSource = null;
        MiniArtBrush.ImageSource = null;
        ResetAccent();
    }

    private static ArtworkResult? DecodeArtwork(byte[] bytes)
    {
        var image = DecodeImage(bytes);
        if (image == null) return null;

        var (a, b, c) = ExtractPalette(image);
        return new ArtworkResult(image, a, b, c);
    }

    private static BitmapImage? DecodeImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 512;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static (Color A, Color B, Color C) ExtractPalette(BitmapSource source)
    {
        try
        {
            const int size = 24;
            var scaled = new TransformedBitmap(source,
                new ScaleTransform(size / (double)source.PixelWidth, size / (double)source.PixelHeight));
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            int stride = size * 4;
            var pixels = new byte[size * stride];
            converted.CopyPixels(pixels, stride, 0);

            double sumR = 0, sumG = 0, sumB = 0, totalWeight = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                double b = pixels[i];
                double g = pixels[i + 1];
                double r = pixels[i + 2];
                double max = Math.Max(r, Math.Max(g, b));
                double min = Math.Min(r, Math.Min(g, b));
                if (max < 42) continue;

                double saturation = (max - min) / max;
                if (saturation < 0.14) continue;

                double weight = saturation * (max / 255.0);
                sumR += r * weight;
                sumG += g * weight;
                sumB += b * weight;
                totalWeight += weight;
            }

            if (totalWeight < 2)
                return (DefaultAccentA, DefaultAccentB, DefaultAccentC);

            var dominant = Color.FromRgb(
                (byte)Math.Clamp(sumR / totalWeight, 0, 255),
                (byte)Math.Clamp(sumG / totalWeight, 0, 255),
                (byte)Math.Clamp(sumB / totalWeight, 0, 255));

            var (h, s, l) = RgbToHsl(dominant);
            s = Math.Clamp(s * 1.15 + 0.16, 0.55, 0.86);
            l = Math.Clamp(l, 0.56, 0.72);

            return (
                HslToColor(h, s, l),
                HslToColor(h + 18, Math.Min(0.9, s + 0.04), l * 0.92),
                HslToColor(h - 14, s * 0.92, l * 0.82));
        }
        catch
        {
            return (DefaultAccentA, DefaultAccentB, DefaultAccentC);
        }
    }

    private static (double H, double S, double L) RgbToHsl(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        double h = 0, s = 0;

        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;

            h *= 60;
        }

        return (h, s, l);
    }

    private static Color HslToColor(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static ulong QuickHash(byte[] bytes)
    {
        ulong hash = 1469598103934665603;
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211;
        }
        return hash;
    }

    #endregion

    #region Transport commands

    private async void BtnPlayPause_Click(object sender, RoutedEventArgs e)
    {
        await TogglePlayPauseAsync();
    }

    private async Task TogglePlayPauseAsync()
    {
        bool ok = await _music.TogglePlayPauseAsync();
        if (!ok)
            _mediaKeys.TogglePlayPause();

        _isPlaying = !_isPlaying;
        UpdatePlaybackState(_isPlaying ? PlaybackState.Playing : PlaybackState.Paused);
    }

    private async void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        await NextAsync();
    }

    private async Task NextAsync()
    {
        if (!await _music.SkipNextAsync())
            _mediaKeys.NextTrack();
    }

    private async void BtnPrevious_Click(object sender, RoutedEventArgs e)
    {
        await PreviousAsync();
    }

    private async Task PreviousAsync()
    {
        if (!await _music.SkipPreviousAsync())
            _mediaKeys.PreviousTrack();
    }

    private async void BtnShuffle_Click(object sender, RoutedEventArgs e)
    {
        if (!_supportsShuffle)
        {
            ShowToast("当前播放器不支持随机切换");
            return;
        }

        bool target = !_shuffleActive;
        if (await _music.SetShuffleAsync(target))
        {
            SetShuffleVisual(target);
            ShowToast(target ? "随机播放：开" : "随机播放：关");
        }
        else
        {
            ShowToast("切换随机播放失败");
        }
    }

    private void BtnLike_Click(object sender, RoutedEventArgs e)
    {
        if (!_track.IsQQMusic)
        {
            ShowToast("收藏仅支持 QQ 音乐");
            return;
        }

        ShowToast(_qqMusic.ToggleLike() ? "已发送收藏快捷键" : "QQ 音乐未运行");
    }

    private void BtnPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (!QQMusicHotkeyService.OpenPlayer(_track.SourceAppId, _track.SourceName))
            ShowToast("未能启动播放器");
    }

    private void BtnMute_Click(object sender, RoutedEventArgs e)
    {
        if (!_volume.TryToggleMute(out bool muted))
        {
            ShowToast("无法调节系统音量");
            return;
        }

        _isMuted = muted;
        UpdateVolumeVisual();
        ShowToast(muted ? "已静音" : "已取消静音");
    }

    private void SliderSeek_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = true;
    }

    private async void SliderSeek_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        await CommitSeekAsync();
    }

    private async void SliderSeek_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isSeeking)
            await CommitSeekAsync();
    }

    private async Task CommitSeekAsync()
    {
        if (!_isSeeking) return;
        _isSeeking = false;

        if (_track.Duration <= TimeSpan.Zero)
            return;

        var target = TimeSpan.FromSeconds(SliderSeek.Value);
        _music.NotifySeekPreview(target);

        if (!await _music.SeekAsync(target))
            ShowToast("当前播放器不支持拖动进度");
    }

    private void SliderSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtElapsed is null) return;
        if (_isSeeking)
            TxtElapsed.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
    }

    private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volume is null || TxtVolume is null) return;
        if (_suppressVolume) return;

        int value = (int)Math.Round(e.NewValue);
        TxtVolume.Text = $"{value}%";

        if (_volume.TrySetVolume(value))
        {
            if (_isMuted && value > 0)
            {
                _volume.TryToggleMute(out bool muted);
                _isMuted = muted;
                UpdateVolumeVisual();
            }
        }
    }

    private void SliderVolume_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SliderVolume.Value = Math.Clamp(SliderVolume.Value + (e.Delta > 0 ? 2 : -2), 0, 100);
        e.Handled = true;
    }

    private void RefreshVolume()
    {
        if (!_volume.TryGetVolume(out int percent, out bool muted))
        {
            TxtVolume.Text = "--";
            return;
        }

        _suppressVolume = true;
        SliderVolume.Value = percent;
        _suppressVolume = false;
        _isMuted = muted;
        TxtVolume.Text = $"{percent}%";
        UpdateVolumeVisual();
    }

    private void UpdateVolumeVisual()
    {
        VolumeIcon.Data = Geometry.Parse(_isMuted ? VolumeMutedIconData : VolumeIconData);
        BtnMute.Foreground = _isMuted
            ? TryFindResource("AccentBrush") as Brush ?? Brushes.White
            : TryFindResource("TextSecondary") as Brush ?? Brushes.Gray;
        BtnMute.ToolTip = _isMuted ? "取消静音" : "静音";
    }

    #endregion

    #region Title bar / panel controls

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        _edge.SetPinned(_isPinned);
        UpdatePinVisual();
        ShowToast(_isPinned ? "面板已固定" : "面板将自动隐藏");

        if (_isPinned)
            StartFullscreenWatch();
    }

    private void UpdatePinVisual()
    {
        BtnPin.Foreground = _isPinned
            ? TryFindResource("AccentBrush") as Brush ?? Brushes.White
            : TryFindResource("TextSecondary") as Brush ?? Brushes.Gray;
        BtnPin.ToolTip = _isPinned ? "取消固定" : "固定面板";
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = false;
        _edge.SetPinned(false);
        UpdatePinVisual();
        HidePanel();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsDialog();
    }

    public void OpenSettingsDialog()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(OpenSettingsDialog);
            return;
        }

        var dialog = new SettingsWindow(_settings.Clone())
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true
        };

        if (dialog.ShowDialog() != true || dialog.ResultSettings == null) return;

        _settings = dialog.ResultSettings;
        SettingsService.Save(_settings);
        ApplySettings();
        ShowToast("设置已保存");
    }

    #endregion

    #region Tray API

    public void ShowPanelFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowPanelFromTray);
            return;
        }

        CancelAnimation();
        _isPinned = true;
        _edge.SetPinned(true);
        UpdatePinVisual();
        Opacity = 1;
        _music.IsPanelVisible = true;

        if (_state == PanelState.Hidden)
            SnapToState(PanelState.Hidden);

        ShowExpanded();
    }

    public void HidePanelFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(HidePanelFromTray);
            return;
        }

        _isPinned = false;
        _edge.SetPinned(false);
        UpdatePinVisual();
        CancelAnimation();
        HidePanel();
    }

    public void TogglePlayPauseFromTray()
    {
        Dispatcher.BeginInvoke(new Action(async () => await TogglePlayPauseAsync()));
    }

    public void NextFromTray()
    {
        Dispatcher.BeginInvoke(new Action(async () => await NextAsync()));
    }

    public void PreviousFromTray()
    {
        Dispatcher.BeginInvoke(new Action(async () => await PreviousAsync()));
    }

    public void ToggleLikeFromTray()
    {
        Dispatcher.BeginInvoke(new Action(() => BtnLike_Click(this, new RoutedEventArgs())));
    }

    public void FocusPanelWindow()
    {
        ShowPanelFromTray();
    }

    #endregion

    #region Helpers

    private void ShowToast(string message)
    {
        TxtToast.Text = message;
        ToastHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1700) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            ToastHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)));
        };
        _toastTimer.Start();
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }

    private sealed record ArtworkResult(BitmapImage Image, Color AccentA, Color AccentB, Color AccentC);

    #endregion
}
