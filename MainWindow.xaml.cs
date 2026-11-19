using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
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
    private readonly QQMusicHotkeyService _qqMusic;
    private readonly object _artworkGate = new();
    private readonly Dictionary<string, ArtworkResult?> _artworkCache = new();
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
    private RECT _animWork;
    private int _lastFrameX;
    private Action? _animOnComplete;
    private bool _animFrameHooked;

    private TrackInfo _track = new();
    private bool _isSeeking;
    private bool _seekPending;
    private long _seekVersion;
    private bool _isMuted;
    private bool _suppressVolume;
    private byte[]? _artworkBytes;
    private long _artVersion;
    private int _activeLyricIndex = int.MinValue;
    private LyricLine? _activeLyricLine;
    private string? _lastRailLyric;
    private bool _lastRailHighlight;

    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _fullscreenTimer;

    private static readonly Color DefaultAccent = Color.FromRgb(0xCD, 0xB1, 0xFF);
    private static readonly Geometry PlayGeometry = ParseFrozenGeometry("M8,5v14l11,-7z");
    private static readonly Geometry PauseGeometry = ParseFrozenGeometry("M6,19h4V5H6v14zm8,-14v14h4V5h-4z");

    private const string VolumeIconData =
        "M4,9 H8 L12,5 V19 L8,15 H4 Z M15.5,9.5 C16.5,10.5 17,11.2 17,12 C17,12.8 16.5,13.5 15.5,14.5 M18,7 C20,8.5 21,10.1 21,12 C21,13.9 20,15.5 18,17";
    private const string VolumeMutedIconData =
        "M4,9 H8 L12,5 V19 L8,15 H4 Z M16,9 L21,15 M21,9 L16,15";

    public MainWindow(AppSettings settings, bool showOnStartup = false)
    {
        _settings = settings;
        _showOnStartup = showOnStartup;

        _music = new MusicSourceManager(settings);
        _qqMusic = new QQMusicHotkeyService();
        _edge = new EdgeDetector();
        _volume = new SystemVolumeService();

        InitializeComponent();

        _music.TrackChanged += OnTrackChanged;
        _music.PositionChanged += UpdatePosition;
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
        Interlocked.Increment(ref _artVersion);
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

    private int GetTargetX(PanelState state, RECT? workArea = null)
    {
        var work = workArea ?? GetWorkArea();
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

    private void SetPanelX(int x, RECT work)
    {
        if (_hwnd == IntPtr.Zero) return;
        int panelHeight = (int)Math.Round(PanelHeight * _dpiY);
        int top = work.Top + Math.Max(0, (work.Height - panelHeight) / 2);

        SetWindowPos(_hwnd, IntPtr.Zero, x, top, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        UpdateEdgeLinePosition(x, work);
    }

    private void UpdateEdgeLinePosition(int windowX, RECT work)
    {
        bool left = _settings.EdgeSide == "Left";
        double local = (left ? work.Left - windowX : work.Right - windowX) / _dpiX;
        EdgeProgressShift.X = left ? local : local - EdgeProgressHost.Width;
    }

    private void SnapToState(PanelState state)
    {
        var work = GetWorkArea();
        Height = Math.Min(PanelHeight, work.Height / _dpiY);
        SetPanelX(GetTargetX(state, work), work);
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
        ToastHost.Margin = left ? new Thickness(0, 0, 56, 65) : new Thickness(56, 0, 0, 65);
        PanelBorder.BorderThickness = left
            ? new Thickness(0, 1, 1, 1)
            : new Thickness(1, 1, 0, 1);
        Rail.BorderThickness = left
            ? new Thickness(1, 0, 0, 0)
            : new Thickness(0, 0, 1, 0);

        UpdateEdgeLinePosition(GetWindowX(), GetWorkArea());
        if (_lastRailLyric is { } text)
        {
            _lastRailLyric = null;
            UpdateRailLyric(text, _lastRailHighlight);
        }
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

        SnapToState(_state);
        UpdateBridgeStatus();
        _activeLyricIndex = int.MinValue;
        if (_state != PanelState.Hidden)
            UpdatePosition(_music.CurrentPosition);
        StopLyricTransition();

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

        _animWork = GetWorkArea();
        Height = Math.Min(PanelHeight, _animWork.Height / _dpiY);
        _animFromX = GetWindowX();
        _lastFrameX = _animFromX;
        _animToX = GetTargetX(target, _animWork);
        if (!SystemParameters.ClientAreaAnimation || _animFromX == _animToX)
        {
            SetPanelX(_animToX, _animWork);
            onComplete?.Invoke();
            return;
        }

        _animFrameHooked = true;
        _animating = true;
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
        if (x != _lastFrameX)
        {
            _lastFrameX = x;
            SetPanelX(x, _animWork);
        }

        if (progress < 1) return;

        FinishAnimation();
    }

    private void FinishAnimation()
    {
        if (!_animFrameHooked) return;

        CompositionTarget.Rendering -= OnAnimationFrame;
        _animFrameHooked = false;
        _animating = false;
        if (_lastFrameX != _animToX) SetPanelX(_animToX, _animWork);

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
        StopLyricTransition();
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
        _music.IsPanelVisible = false;
        StopLyricTransition();
        AnimateTo(PanelState.Hidden, HideDurationMs, () =>
        {
            if (_state != PanelState.Hidden) return;
            Opacity = 0;
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

    private void OnTrackChanged(TrackInfo track) => UpdateTrack(track);

    private void OnSourceStatusChanged(PlayerCapConnection connection) => UpdateBridgeStatus();

    private void UpdateTrack(TrackInfo track)
    {
        var previous = _track;
        bool songChanged = previous.SourceAppId != track.SourceAppId || previous.Title != track.Title ||
                           previous.Artist != track.Artist || previous.Album != track.Album;
        bool lyricsChanged = !ReferenceEquals(previous.Lyrics, track.Lyrics);
        _track = track;

        if (songChanged)
        {
            _isSeeking = _seekPending = false;
            ++_seekVersion;
            _activeLyricLine = null;
            StopLyricTransition();
            TrackHeader.Visibility = track.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            TxtSource.Text = track.IsEmpty ? "待机" : track.SourceName;
            TxtSongTitle.Text = track.IsEmpty ? "未在播放" : track.Title;
            TxtSongTitle.ToolTip = track.IsEmpty ? null : track.Title;
            TxtArtist.Text = track.IsEmpty ? "MusicEdgeX" : BuildSubtitle(track);
            TxtArtist.ToolTip = track.IsEmpty ? null : TxtArtist.Text;
            BtnOpenPlayer.Visibility = track.SourceName is "QQ 音乐" or "网易云音乐" or "Spotify"
                ? Visibility.Visible : Visibility.Collapsed;
            BtnOpenPlayer.ToolTip = $"打开{track.SourceName}";
            BtnLike.IsEnabled = !track.IsEmpty && track.IsQQMusic;
        }

        if (songChanged || previous.State != track.State || previous.Controls != track.Controls)
            UpdatePlaybackControls();
        if (songChanged || previous.Duration != track.Duration || previous.Controls != track.Controls)
        {
            SliderSeek.Maximum = Math.Max(1, track.Duration.TotalSeconds);
            SliderSeek.IsEnabled = track.Duration > TimeSpan.Zero && track.CanControl(PlaybackControls.Seek);
            TxtTotal.Text = track.Duration > TimeSpan.Zero ? FormatTime(track.Duration) : "--:--";
        }

        if (previous.IsShuffleActive != track.IsShuffleActive)
            SetShuffleVisual();

        if (songChanged || lyricsChanged)
        {
            _activeLyricIndex = int.MinValue;
            if (track.Lyrics.Count == 0)
                ShowLyricFallback();
        }

        if (songChanged || lyricsChanged || previous.LyricSource != track.LyricSource ||
            previous.HasMediaSession != track.HasMediaSession)
            UpdateBridgeStatus();

        if (songChanged || lyricsChanged || _state != PanelState.Hidden)
            UpdatePosition(track.Position);
        if (songChanged || !ReferenceEquals(_artworkBytes, track.ThumbnailBytes))
            UpdateArtwork(track.ThumbnailBytes, songChanged);
    }

    private void UpdateBridgeStatus()
    {
        string text = _music.Connection switch
        {
            PlayerCapConnection.Connected when _track.IsEmpty => "歌词桥接已连接",
            PlayerCapConnection.Connected when _track.LyricSource.Length == 0 => "歌词桥接已连接 · 等待匹配曲目",
            PlayerCapConnection.Connected when _track.Lyrics.Count == 0 => "歌词桥接已连接 · 暂无歌词",
            PlayerCapConnection.Connected => "歌词桥接已连接",
            PlayerCapConnection.Probing or PlayerCapConnection.Connecting => "正在连接歌词桥接",
            PlayerCapConnection.Unreachable or PlayerCapConnection.Disconnected => "歌词桥接未连接 · 将自动重试",
            _ => _settings.UsePlayerCap ? "等待歌词桥接连接" : "歌词桥接已关闭"
        };

        if (!_track.IsEmpty && !_track.HasMediaSession)
            text = "仅显示歌词 · " + text;
        if (text != TxtBridgeStatus.Text) TxtBridgeStatus.Text = text;
        TxtBridgeStatus.ToolTip = text;
    }

    private void ShowLyricFallback()
    {
        _activeLyricLine = null;
        StopLyricTransition();
        var title = _track.IsEmpty ? "等一首歌" : "暂无歌词";
        var hint = _track.IsEmpty ? "请在 QQ 音乐、网易云音乐等客户端开始播放。" : "";
        SetLyricTexts("", title, hint, "");
        LyricAccent.Opacity = 0.65;
        UpdateRailLyric(_track.IsEmpty ? "待机" : _track.Title, highlight: false);
    }

    private void UpdateLyricForPosition(TimeSpan position)
    {
        var lyrics = _track.Lyrics;
        if (lyrics.Count == 0) return;

        int index = LyricTiming.FindLine(lyrics, position, _settings.LyricOffsetMs);
        if (index == _activeLyricIndex) return;
        _activeLyricIndex = index;

        var previous = index > 0 ? lyrics[index - 1] : null;
        var current = index >= 0 ? lyrics[index] : null;
        var next = index + 1 < lyrics.Count ? lyrics[index + 1] : null;
        bool changed = current?.Start != _activeLyricLine?.Start || current?.Text != _activeLyricLine?.Text;
        _activeLyricLine = current;
        SetLyricTexts(previous?.Text ?? "", current?.Text ?? "等待歌词", current?.Translation ?? "", next?.Text ?? "");
        LyricAccent.Opacity = current == null ? 0.65 : 1;
        UpdateRailLyric(current?.Text ?? _track.Title, highlight: current != null);
        if (changed && current != null && _state == PanelState.Expanded && _track.State == PlaybackState.Playing &&
            !_isSeeking && !_seekPending && SystemParameters.ClientAreaAnimation)
            AnimateLyricTransition();
    }

    private void AnimateLyricTransition()
    {
        var sweep = new DoubleAnimation(-0.48, 0.82, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        LyricLightShift.BeginAnimation(TranslateTransform.XProperty, sweep);
        LyricLightLayer.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            FillBehavior = FillBehavior.Stop,
            KeyFrames =
            {
                new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new SplineDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(96)),
                    new KeySpline(0.22, 0.75, 0.50, 1)),
                new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)))
            }
        });
        var settle = TimeSpan.FromMilliseconds(180);
        TxtLyricCurrent.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.82, 1, settle) { FillBehavior = FillBehavior.Stop });
        ((TranslateTransform)TxtLyricCurrent.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(3, 0, settle) { FillBehavior = FillBehavior.Stop });
    }

    private void StopLyricTransition()
    {
        LyricLightShift.BeginAnimation(TranslateTransform.XProperty, null);
        LyricLightLayer.BeginAnimation(OpacityProperty, null);
        TxtLyricCurrent.BeginAnimation(OpacityProperty, null);
        ((TranslateTransform)TxtLyricCurrent.RenderTransform).BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void SetLyricTexts(string previous, string current, string translation, string next)
    {
        TxtLyricPrev.Text = previous;
        TxtLyricPrev.Visibility = previous.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TxtLyricCurrent.Text = current;
        TxtLyricCurrent.ToolTip = current;
        TxtLyricTranslation.Text = translation;
        TxtLyricTranslation.Visibility = translation.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TxtLyricNext.Text = next;
        TxtLyricNext.Visibility = next.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateRailLyric(string text, bool highlight)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (_lastRailLyric == text && _lastRailHighlight == highlight) return;
        _lastRailLyric = text;
        _lastRailHighlight = highlight;

        bool vertical = ContainsCjk(text);
        TxtRailLyric.Width = vertical ? double.NaN : 152;
        TxtRailLyric.LayoutTransform = vertical
            ? Transform.Identity
            : new RotateTransform(_settings.EdgeSide == "Left" ? 90 : -90);
        TxtRailLyric.ToolTip = text;
        TxtRailLyric.Foreground = highlight
            ? (Brush)FindResource("TextPrimary")
            : (Brush)FindResource("TextSecondary");
        RailLyricMark.Opacity = highlight ? 1 : 0.55;

        if (!vertical)
        {
            TxtRailLyric.Text = text;
            return;
        }

        var characters = StringInfo.GetTextElementEnumerator(text);
        var visible = new List<string>(8);
        while (characters.MoveNext())
        {
            var character = characters.GetTextElement();
            if (string.IsNullOrWhiteSpace(character)) continue;
            if (visible.Count == 7)
            {
                visible.Add("…");
                break;
            }
            visible.Add(character);
        }
        TxtRailLyric.Text = string.Join('\n', visible);
    }

    private static bool ContainsCjk(string text) => text.Any(ch =>
        ch is >= '\u3400' and <= '\u9FFF' or >= '\u3040' and <= '\u30FF' or >= '\uAC00' and <= '\uD7AF');

    private void UpdatePosition(TimeSpan position)
    {
        if (!_isSeeking && !_seekPending)
        {
            var elapsed = _track.IsEmpty ? "--:--" : FormatTime(position);
            if (TxtElapsed.Text != elapsed) TxtElapsed.Text = elapsed;
            if (!_track.IsEmpty)
                SliderSeek.Value = Math.Clamp(position.TotalSeconds, 0, SliderSeek.Maximum);
            else
                SliderSeek.Value = 0;
            UpdateLyricForPosition(position);
            UpdateEdgeProgress(position);
        }
    }

    private void UpdatePlaybackControls()
    {
        bool playing = _track.State == PlaybackState.Playing && !_track.IsEmpty;
        PlayIcon.Data = RailPlayIcon.Data = playing ? PauseGeometry : PlayGeometry;
        BtnPlayPause.IsEnabled = BtnRailPlay.IsEnabled = _track.CanControl(PlaybackControls.PlayPause);
        BtnPlayPause.ToolTip = BtnRailPlay.ToolTip = playing ? "暂停" : "播放";
        BtnPrevious.IsEnabled = _track.CanControl(PlaybackControls.Previous);
        BtnNext.IsEnabled = _track.CanControl(PlaybackControls.Next);
        BtnShuffle.IsEnabled = _track.CanControl(PlaybackControls.Shuffle);
        TxtSource.Text = _track.IsEmpty ? "待机" :
            _track.State == PlaybackState.Paused ? $"{_track.SourceName} · 已暂停" : _track.SourceName;
        UpdateRailPlayingDot();
    }

    private static Geometry ParseFrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private void UpdateRailPlayingDot() =>
        RailPlayingDot.Opacity = _track.State == PlaybackState.Playing && !_track.IsEmpty ? 1 : 0;

    private void UpdateEdgeProgress(TimeSpan position)
    {
        bool hasProgress = !_track.IsEmpty && _track.Duration > TimeSpan.Zero;
        if (EdgeProgressFill.RenderTransform is ScaleTransform scale)
            scale.ScaleY = hasProgress ? Math.Clamp(position / _track.Duration, 0, 1) : 0;
        EdgeProgressFill.Opacity = hasProgress ? 1 : 0;
    }

    private void SetShuffleVisual()
    {
        BtnShuffle.Foreground = (Brush)FindResource(_track.IsShuffleActive ? "AccentBrush" : "TextSecondary");
        BtnShuffle.ToolTip = _track.IsShuffleActive ? "随机播放：开" : "随机播放：关";
    }

    private async void UpdateArtwork(byte[]? bytes, bool songChanged)
    {
        if (ReferenceEquals(bytes, _artworkBytes)) return;
        _artworkBytes = bytes;
        long version = Interlocked.Increment(ref _artVersion);
        if (songChanged || bytes == null)
            ApplyArtwork(null);
        if (bytes == null) return;

        var result = await Task.Run(() =>
        {
            lock (_artworkGate)
            {
                if (version != Volatile.Read(ref _artVersion)) return null;
                string key = Convert.ToHexString(SHA256.HashData(bytes));
                if (!_artworkCache.TryGetValue(key, out var artwork))
                {
                    artwork = DecodeArtwork(bytes);
                    if (_artworkCache.Count >= 8)
                        _artworkCache.Remove(_artworkCache.Keys.First());
                    _artworkCache.Add(key, artwork);
                }
                return artwork;
            }
        });
        if (version == _artVersion) ApplyArtwork(result);
    }

    private void ApplyArtwork(ArtworkResult? artwork)
    {
        AlbumArtBrush.ImageSource = MiniArtBrush.ImageSource = artwork?.Image;
        AlbumArtLayer.Opacity = MiniArtLayer.Opacity = artwork != null ? 1 : 0;
        var accent = artwork?.Accent ?? DefaultAccent;
        Resources["AccentBrush"] = new SolidColorBrush(accent);
        LyricLightOuter.Color = Color.FromArgb(0x12, accent.R, accent.G, accent.B);
        LyricLightCenter.Color = Color.FromArgb(0x44, accent.R, accent.G, accent.B);

        SetShuffleVisual();
        UpdatePinVisual();
        UpdateVolumeVisual();
    }

    private static ArtworkResult? DecodeArtwork(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None).Frames[0];
            int width = frame.PixelWidth;
            int height = frame.PixelHeight;
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (Math.Max(width, height) > 160)
            {
                if (width >= height) image.DecodePixelWidth = 160;
                else image.DecodePixelHeight = 160;
            }
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return new ArtworkResult(image, ExtractAccent(image));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    private static Color ExtractAccent(BitmapSource source)
    {
        try
        {
            const int size = 24;
            var scaled = new TransformedBitmap(source,
                new ScaleTransform(size / (double)source.PixelWidth, size / (double)source.PixelHeight));
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            int stride = size * 4;
            var pixels = new byte[size * stride];
            converted.CopyPixels(pixels, stride, 0);

            double sumR = 0, sumG = 0, sumB = 0, totalWeight = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                double max = Math.Max(r, Math.Max(g, b));
                double min = Math.Min(r, Math.Min(g, b));
                double weight = (max - min) / 255 * (pixels[i + 3] / 255.0);
                if (max < 42 || weight < 0.09) continue;
                sumR += r * weight;
                sumG += g * weight;
                sumB += b * weight;
                totalWeight += weight;
            }
            if (totalWeight < 2) return DefaultAccent;

            var dominant = Color.FromRgb((byte)(sumR / totalWeight),
                (byte)(sumG / totalWeight), (byte)(sumB / totalWeight));
            var (hue, saturation, _) = RgbToHsl(dominant);
            return HslToColor(hue, Math.Clamp(saturation, 0.38, 0.72), 0.77);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return DefaultAccent;
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

    #endregion

    #region Transport commands

    private async void BtnPlayPause_Click(object sender, RoutedEventArgs e)
    {
        await TogglePlayPauseAsync();
    }

    private async Task TogglePlayPauseAsync()
    {
        if (!await _music.TogglePlayPauseAsync()) ShowTransportUnavailable("播放或暂停");
    }

    private async void BtnNext_Click(object sender, RoutedEventArgs e) => await NextAsync();

    private async Task NextAsync()
    {
        if (!await _music.SkipNextAsync()) ShowTransportUnavailable("下一首");
    }

    private async void BtnPrevious_Click(object sender, RoutedEventArgs e) => await PreviousAsync();

    private async Task PreviousAsync()
    {
        if (!await _music.SkipPreviousAsync()) ShowTransportUnavailable("上一首");
    }

    private async void BtnShuffle_Click(object sender, RoutedEventArgs e)
    {
        if (!await _music.SetShuffleAsync(!_track.IsShuffleActive))
            ShowTransportUnavailable("切换随机播放");
    }

    private void ShowTransportUnavailable(string action)
    {
        if (_state != PanelState.Hidden)
            ShowToast(_track.HasMediaSession ? $"播放器未能执行{action}" : "当前没有可控制的音乐会话");
    }

    private void BtnLike_Click(object sender, RoutedEventArgs e)
    {
        if (_track.IsEmpty || !_track.IsQQMusic)
        {
            if (_state != PanelState.Hidden) ShowToast("收藏仅支持正在显示的 QQ 音乐曲目");
            return;
        }

        if (_qqMusic.ToggleLike())
        {
            FlashLike();
            ShowToast("已发送收藏快捷键");
        }
        else
        {
            ShowToast("QQ 音乐未运行");
        }
    }

    private void FlashLike()
    {
        var original = BtnLike.Foreground;
        BtnLike.Foreground = TryFindResource("AccentBrush") as Brush ?? Brushes.White;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(520) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            BtnLike.Foreground = original;
        };
        timer.Start();
    }

    private void BtnPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (!QQMusicHotkeyService.OpenPlayer(_track.SourceName))
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
        if (_seekPending) e.Handled = true;
        else
        {
            _isSeeking = true;
            StopLyricTransition();
        }
    }

    private async void SliderSeek_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        await CommitSeekAsync();

    private async void SliderSeek_LostMouseCapture(object sender, MouseEventArgs e) =>
        await CommitSeekAsync();

    private async void SliderSeek_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!SliderSeek.IsMouseCaptureWithin)
            await CommitSeekAsync();
    }

    private async void SliderSeek_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        double? offset = e.Key switch
        {
            Key.Left or Key.Down => -SliderSeek.SmallChange,
            Key.Right or Key.Up => SliderSeek.SmallChange,
            Key.PageDown => -SliderSeek.LargeChange,
            Key.PageUp => SliderSeek.LargeChange,
            Key.Home => -SliderSeek.Value,
            Key.End => SliderSeek.Maximum - SliderSeek.Value,
            _ => null
        };
        if (offset == null) return;
        e.Handled = true;
        if (_seekPending) return;

        _isSeeking = true;
        SliderSeek.Value = Math.Clamp(SliderSeek.Value + offset.Value, 0, SliderSeek.Maximum);
        await CommitSeekAsync();
    }

    private async Task CommitSeekAsync()
    {
        if (!_isSeeking) return;
        _isSeeking = false;
        _seekPending = true;

        long version = ++_seekVersion;
        bool success = await _music.SeekAsync(TimeSpan.FromSeconds(SliderSeek.Value));
        if (version != _seekVersion) return;
        _seekPending = false;
        UpdatePosition(_music.CurrentPosition);
        if (!success) ShowToast("跳转失败，已恢复播放位置");
    }

    private void SliderSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isSeeking) return;
        var preview = TimeSpan.FromSeconds(e.NewValue);
        TxtElapsed.Text = FormatTime(preview);
        UpdateLyricForPosition(preview);
        UpdateEdgeProgress(preview);
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
        int style = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, style & ~WS_EX_NOACTIVATE);
        try
        {
            Activate();
            BtnSettings.Focus();
        }
        finally
        {
            SetWindowLong(_hwnd, GWL_EXSTYLE, style);
        }
    }

    #endregion

    #region Helpers

    private void ShowToast(string message)
    {
        TxtToast.Text = message;
        SetToastOpacity(1);

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1700) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            SetToastOpacity(0);
        };
        _toastTimer.Start();
    }

    private void SetToastOpacity(double opacity)
    {
        ToastHost.BeginAnimation(OpacityProperty, null);
        if (!SystemParameters.ClientAreaAnimation)
        {
            ToastHost.Opacity = opacity;
            return;
        }

        double previous = ToastHost.Opacity;
        ToastHost.Opacity = opacity;
        ToastHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(previous, opacity, TimeSpan.FromMilliseconds(140))
            { FillBehavior = FillBehavior.Stop });
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }

    private static string BuildSubtitle(TrackInfo track)
    {
        string artist = string.IsNullOrWhiteSpace(track.Artist)
            ? "未知艺术家"
            : track.Artist.Trim();
        string album = track.Album?.Trim() ?? string.Empty;

        return string.IsNullOrWhiteSpace(album) ? artist : $"{artist} · {album}";
    }

    private sealed record ArtworkResult(BitmapImage Image, Color Accent);

    #endregion
}
