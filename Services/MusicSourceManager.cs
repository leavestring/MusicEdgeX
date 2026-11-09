using System.Windows.Threading;
using MusicEdge.Models;

namespace MusicEdge.Services;

/// <summary>
/// Merges a zero-config SMTC source with the optional PlayerCap bridge.
/// Metadata, cover and lyrics prefer PlayerCap when it is the active player;
/// transport commands always go through SMTC (with media-key fallback in the UI).
/// </summary>
public sealed class MusicSourceManager : IDisposable
{
    public event Action<TrackInfo>? TrackChanged;
    public event Action<PlaybackState>? PlaybackStateChanged;
    public event Action<TimeSpan>? PositionChanged;
    public event Action<bool>? ShuffleChanged;
    public event Action<IReadOnlyList<LyricLine>>? LyricsChanged;
    public event Action<PlayerCapConnection, string>? SourceStatusChanged;

    private readonly SMTCMusicService _smtc;
    private readonly DispatcherTimer _ticker;
    private readonly object _gate = new();
    private AppSettings _settings;
    private PlayerCapService? _cap;
    private TrackInfo _current = new() { State = PlaybackState.Closed };
    private PlaybackState _lastState = PlaybackState.Closed;
    private string _lastTrackKey = string.Empty;
    private bool _capActive;
    private bool _panelVisible;
    private bool _running;
    private bool _disposed;

    public MusicSourceManager(AppSettings settings)
    {
        _settings = settings.Clone();
        _smtc = new SMTCMusicService();
        _ticker = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _ticker.Tick += (_, _) => PublishPosition();

        _smtc.TrackChanged += OnSmtcTrackChanged;
        _smtc.PlaybackStateChanged += OnSmtcStateChanged;
        _smtc.ShuffleChanged += active => ShuffleChanged?.Invoke(active);
    }

    public TrackInfo CurrentTrack => _current;

    public bool IsPlayerCapActive
    {
        get { lock (_gate) return _capActive; }
    }

    public PlayerCapConnection PlayerCapConnection =>
        _cap?.Connection ?? PlayerCapConnection.Idle;

    public string PlayerCapPlayer => _cap?.ActivePlayer ?? string.Empty;

    public bool IsPanelVisible
    {
        get => _panelVisible;
        set
        {
            if (_panelVisible == value) return;
            _panelVisible = value;
            _smtc.IsPanelVisible = false;

            if (value)
            {
                _ticker.Start();
                PublishPosition();
            }
            else
            {
                _ticker.Stop();
            }
        }
    }

    public void Start()
    {
        if (_running || _disposed) return;
        _running = true;

        _smtc.Start();
        RebuildPlayerCap();
        PublishMerged(forceTrack: true, forceState: true);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _ticker.Stop();
        _smtc.Stop();

        if (_cap != null)
        {
            UnsubscribeCap(_cap);
            _cap.Stop();
            _cap.Dispose();
            _cap = null;
        }
    }

    public void UpdateSettings(AppSettings settings)
    {
        bool hostChanged = !string.Equals(_settings.PlayerCapHost, settings.PlayerCapHost,
            StringComparison.OrdinalIgnoreCase);
        bool playerChanged = !string.Equals(_settings.PlayerCapPlayer, settings.PlayerCapPlayer,
            StringComparison.Ordinal);
        bool enabledChanged = _settings.UsePlayerCap != settings.UsePlayerCap;

        _settings = settings.Clone();

        if (!_running) return;
        if (hostChanged || playerChanged || enabledChanged)
            RebuildPlayerCap();
    }

    public Task<bool> TogglePlayPauseAsync() => _smtc.TogglePlayPauseAsync();

    public Task<bool> SkipNextAsync() => _smtc.SkipNextAsync();

    public Task<bool> SkipPreviousAsync() => _smtc.SkipPreviousAsync();

    public Task<bool> SeekAsync(TimeSpan position) => _smtc.SeekAsync(position);

    public Task<bool> SetShuffleAsync(bool active) => _smtc.SetShuffleAsync(active);

    public void NotifySeekPreview(TimeSpan position) => _smtc.NotifySeekPreview(position);

    private void RebuildPlayerCap()
    {
        if (_cap != null)
        {
            UnsubscribeCap(_cap);
            _cap.Stop();
            _cap.Dispose();
            _cap = null;
        }

        if (!_settings.UsePlayerCap)
        {
            SourceStatusChanged?.Invoke(PlayerCapConnection.Idle, "增强歌词源未启用");
            PublishMerged(forceTrack: true);
            return;
        }

        _cap = new PlayerCapService(_settings.PlayerCapHost, _settings.PlayerCapPlayer);
        _cap.TrackChanged += OnCapTrackChanged;
        _cap.PlaybackStateChanged += OnCapStateChanged;
        _cap.LyricsChanged += OnCapLyricsChanged;
        _cap.ConnectionChanged += OnCapConnectionChanged;
        _cap.Start();
    }

    private void UnsubscribeCap(PlayerCapService cap)
    {
        cap.TrackChanged -= OnCapTrackChanged;
        cap.PlaybackStateChanged -= OnCapStateChanged;
        cap.LyricsChanged -= OnCapLyricsChanged;
        cap.ConnectionChanged -= OnCapConnectionChanged;
    }

    private void OnSmtcTrackChanged(TrackInfo track)
    {
        PublishMerged(forceTrack: true);
    }

    private void OnSmtcStateChanged(PlaybackState state)
    {
        if (!_capActive)
            PlaybackStateChanged?.Invoke(state);
        else
            PublishMerged();
    }

    private void OnCapTrackChanged(TrackInfo track)
    {
        PublishMerged(forceTrack: true);
    }

    private void OnCapStateChanged(PlaybackState state)
    {
        PublishMerged(forceState: true);
    }

    private void OnCapLyricsChanged(IReadOnlyList<LyricLine> lyrics)
    {
        LyricsChanged?.Invoke(lyrics);
        PublishMerged(forceTrack: true);
    }

    private void OnCapConnectionChanged(PlayerCapConnection connection, string detail)
    {
        SourceStatusChanged?.Invoke(connection, detail);
        if (connection != PlayerCapConnection.Connected)
            PublishMerged(forceTrack: true, forceState: true);
    }

    private void PublishMerged(bool forceTrack = false, bool forceState = false)
    {
        var cap = _cap;
        TrackInfo? capTrack = null;

        if (cap != null && cap.Connection == PlayerCapConnection.Connected && cap.HasTrack)
            capTrack = cap.CurrentTrack;

        var smtcTrack = _smtc.CurrentTrack;
        bool capActive = capTrack != null &&
                         (capTrack.State == PlaybackState.Playing ||
                          smtcTrack.IsEmpty ||
                          (smtcTrack.State != PlaybackState.Playing &&
                           capTrack.State == PlaybackState.Paused));

        var source = capActive ? capTrack! : smtcTrack;
        var merged = CloneWithPosition(source, capActive ? cap?.GetPosition() : source.Position);
        merged.SourceKind = capActive ? "PlayerCap" : "SMTC";

        if (!capActive)
        {
            merged.Lyrics = Array.Empty<LyricLine>();
            merged.LyricSource = string.Empty;
        }

        lock (_gate)
        {
            _capActive = capActive;
            _current = merged;
        }

        var key = BuildTrackKey(merged);
        bool trackChanged = forceTrack || !string.Equals(key, _lastTrackKey, StringComparison.Ordinal);
        bool stateChanged = forceState || _lastState != merged.State;

        _lastTrackKey = key;
        _lastState = merged.State;

        if (trackChanged)
            TrackChanged?.Invoke(merged);
        if (stateChanged)
            PlaybackStateChanged?.Invoke(merged.State);
    }

    private void PublishPosition()
    {
        if (!_panelVisible || _disposed) return;

        TimeSpan position;
        if (_capActive && _cap is { Connection: PlayerCapConnection.Connected })
            position = _cap.GetPosition();
        else
            position = _smtc.CurrentTrack.Position;

        var duration = _current.Duration;
        if (duration > TimeSpan.Zero && position > duration)
            position = duration;

        PositionChanged?.Invoke(position);
    }

    private static TrackInfo CloneWithPosition(TrackInfo source, TimeSpan? position) => new()
    {
        Title = source.Title,
        Artist = source.Artist,
        Album = source.Album,
        Year = source.Year,
        Duration = source.Duration,
        Position = position ?? source.Position,
        State = source.State,
        ThumbnailBytes = source.ThumbnailBytes,
        SourceAppId = source.SourceAppId,
        SourceName = source.SourceName,
        SourceKind = source.SourceKind,
        LyricSource = source.LyricSource,
        IsShuffleActive = source.IsShuffleActive,
        SupportsShuffle = source.SupportsShuffle,
        Lyrics = source.Lyrics
    };

    private static string BuildTrackKey(TrackInfo track) =>
        $"{track.SourceKind}\u001f{track.SourceAppId}\u001f{track.Title}\u001f{track.Artist}\u001f" +
        $"{track.Album}\u001f{track.Duration.Ticks}\u001f{track.Lyrics.Count}\u001f{track.State}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _smtc.Dispose();
        _ticker.Stop();
    }
}
