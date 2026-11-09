using System.Diagnostics;
using System.IO;
using Windows.Media.Control;
using MusicEdge.Models;

namespace MusicEdge.Services;

/// <summary>
/// Event-driven SMTC reader with a light polling fallback.
/// Album art is downloaded only when the track identity changes, and playback
/// position is interpolated locally so the seek bar stays smooth without
/// hammering the system media session.
/// </summary>
public sealed class SMTCMusicService : IDisposable
{
    public event Action<TrackInfo>? TrackChanged;
    public event Action<PlaybackState>? PlaybackStateChanged;
    public event Action<bool>? ShuffleChanged;
    public event Action<TimeSpan>? PositionChanged;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly System.Windows.Threading.DispatcherTimer _positionTimer;
    private readonly System.Windows.Threading.DispatcherTimer _fallbackTimer;
    private readonly Stopwatch _positionClock = new();

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private TrackInfo _track = new() { State = PlaybackState.Closed };
    private string _trackKey = string.Empty;
    private string _sessionId = string.Empty;
    private TimeSpan _anchorPosition;
    private TimeSpan _duration;
    private DateTime _lastManagerAttempt = DateTime.MinValue;
    private bool _isRunning;
    private bool _panelVisible;
    private bool _isPlaying;
    private int _refreshing;
    private bool _disposed;

    public SMTCMusicService()
    {
        _positionTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _positionTimer.Tick += (_, _) => PublishPosition();

        _fallbackTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _fallbackTimer.Tick += (_, _) => _ = RefreshAsync();
    }

    public TrackInfo CurrentTrack
    {
        get
        {
            lock (_gate)
            {
                return Snapshot(_track);
            }
        }
    }

    public bool IsPanelVisible
    {
        get => _panelVisible;
        set
        {
            if (_panelVisible == value) return;
            _panelVisible = value;

            if (value)
            {
                _positionTimer.Start();
                PublishPosition();
            }
            else
            {
                _positionTimer.Stop();
            }
        }
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _fallbackTimer.Start();
        _ = RefreshAsync(forceMedia: true);
    }

    public void Stop()
    {
        _isRunning = false;
        _positionTimer.Stop();
        _fallbackTimer.Stop();
        DetachSession();

        if (_manager != null)
        {
            try { _manager.SessionsChanged -= OnSessionsChanged; } catch { }
            _manager = null;
        }
    }

    public async Task RefreshAsync(bool forceMedia = false)
    {
        if (!_isRunning || _disposed) return;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;

        try
        {
            var manager = await GetManagerAsync();
            if (manager == null)
            {
                SetClosed();
                return;
            }

            var session = PickSession(manager);
            if (session == null)
            {
                SetClosed();
                return;
            }

            if (!ReferenceEquals(session, _session))
            {
                DetachSession();
                _session = session;
                AttachSession(session);
                _sessionId = session.SourceAppUserModelId ?? string.Empty;
                forceMedia = true;
            }

            GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback = null;
            try { playback = session.GetPlaybackInfo(); } catch { }

            var state = MapState(playback?.PlaybackStatus);
            var shuffleActive = playback?.IsShuffleActive ?? false;
            var shuffleKnown = playback?.IsShuffleActive != null;

            TimeSpan position = TimeSpan.Zero;
            TimeSpan duration = TimeSpan.Zero;
            try
            {
                var timeline = session.GetTimelineProperties();
                if (timeline != null)
                {
                    position = timeline.Position;
                    duration = timeline.EndTime - timeline.StartTime;
                }
            }
            catch { }

            UpdateTimeline(position, duration, state == PlaybackState.Playing);

            GlobalSystemMediaTransportControlsSessionMediaProperties? media = null;
            try { media = await session.TryGetMediaPropertiesAsync(); } catch { }

            string title = media?.Title ?? string.Empty;
            string artist = media?.Artist ?? string.Empty;
            string album = media?.AlbumTitle ?? string.Empty;
            string sourceId = session.SourceAppUserModelId ?? string.Empty;
            var key = $"{sourceId}\u001f{title}\u001f{artist}\u001f{album}";
            bool mediaChanged = forceMedia || !string.Equals(key, _trackKey, StringComparison.Ordinal);

            byte[]? artwork = null;
            if (mediaChanged)
            {
                artwork = await TryReadThumbnailAsync(media);
            }

            TrackInfo next;
            lock (_gate)
            {
                next = Snapshot(_track);
            }

            bool identityChanged = mediaChanged ||
                                   !string.Equals(next.Title, title, StringComparison.Ordinal) ||
                                   !string.Equals(next.Artist, artist, StringComparison.Ordinal) ||
                                   !string.Equals(next.Album, album, StringComparison.Ordinal) ||
                                   !string.Equals(next.SourceAppId, sourceId, StringComparison.Ordinal);

            if (identityChanged || forceMedia)
            {
                next.Title = title;
                next.Artist = artist;
                next.Album = album;
                next.Duration = duration;
                next.SourceAppId = sourceId;
                next.SourceName = GetSourceName(sourceId);
                next.State = state;
                next.SupportsShuffle = shuffleKnown;
                next.IsShuffleActive = shuffleActive;

                if (artwork != null)
                    next.ThumbnailBytes = artwork;
                else if (mediaChanged)
                    next.ThumbnailBytes = null;

                _trackKey = key;
                StoreTrack(next);
                TrackChanged?.Invoke(Snapshot(next));
            }
            else
            {
                bool stateChanged;
                bool shuffleChanged;
                lock (_gate)
                {
                    stateChanged = _track.State != state;
                    shuffleChanged = _track.IsShuffleActive != shuffleActive ||
                                     _track.SupportsShuffle != shuffleKnown;
                    _track.State = state;
                    _track.IsShuffleActive = shuffleActive;
                    _track.SupportsShuffle = shuffleKnown;
                }

                if (stateChanged)
                    PlaybackStateChanged?.Invoke(state);
                if (shuffleChanged)
                    ShuffleChanged?.Invoke(shuffleActive);
            }

            _isPlaying = state == PlaybackState.Playing;
        }
        catch
        {
            // A transient WinRT failure should not tear down the UI. The next
            // poll or session event will retry.
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    public async Task<bool> TogglePlayPauseAsync()
    {
        var session = GetActiveSession();
        if (session == null) return false;

        try
        {
            var state = MapState(session.GetPlaybackInfo()?.PlaybackStatus);
            bool ok = state == PlaybackState.Playing
                ? await session.TryPauseAsync()
                : await session.TryPlayAsync();

            if (ok)
            {
                _isPlaying = state != PlaybackState.Playing;
                if (_isPlaying)
                {
                    _anchorPosition = GetPosition();
                    _positionClock.Restart();
                }
                await Task.Delay(60);
                await RefreshAsync();
            }

            return ok;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> SkipNextAsync()
    {
        var session = GetActiveSession();
        if (session == null) return false;

        try
        {
            bool ok = await session.TrySkipNextAsync();
            if (ok)
            {
                await Task.Delay(120);
                await RefreshAsync(forceMedia: true);
            }
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> SkipPreviousAsync()
    {
        var session = GetActiveSession();
        if (session == null) return false;

        try
        {
            bool ok = await session.TrySkipPreviousAsync();
            if (ok)
            {
                await Task.Delay(120);
                await RefreshAsync(forceMedia: true);
            }
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> SeekAsync(TimeSpan position)
    {
        var session = GetActiveSession();
        if (session == null) return false;

        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline != null)
            {
                var min = timeline.MinSeekTime;
                var max = timeline.MaxSeekTime;
                if (max > min)
                    position = Clamp(position, min, max);
            }

            bool ok = await session.TryChangePlaybackPositionAsync(position.Ticks);
            if (ok)
            {
                _anchorPosition = position;
                _positionClock.Restart();
                PublishPosition();
            }
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> SetShuffleAsync(bool active)
    {
        var session = GetActiveSession();
        if (session == null) return false;

        try
        {
            var info = session.GetPlaybackInfo();
            if (info?.IsShuffleActive == null) return false;

            bool ok = await session.TryChangeShuffleActiveAsync(active);
            if (ok)
            {
                lock (_gate)
                {
                    _track.IsShuffleActive = active;
                    _track.SupportsShuffle = true;
                }
                ShuffleChanged?.Invoke(active);
            }
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public void NotifySeekPreview(TimeSpan position)
    {
        _anchorPosition = position;
        _positionClock.Restart();
    }

    private void PublishPosition()
    {
        if (!_panelVisible || _disposed) return;
        if (!_isPlaying && _anchorPosition == TimeSpan.Zero) return;

        var position = GetPosition();
        PositionChanged?.Invoke(position);
    }

    private TimeSpan GetPosition()
    {
        var position = _isPlaying
            ? _anchorPosition + _positionClock.Elapsed
            : _anchorPosition;

        if (_duration > TimeSpan.Zero && position > _duration)
            position = _duration;

        return position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }

    private void UpdateTimeline(TimeSpan position, TimeSpan duration, bool playing)
    {
        var interpolated = GetPosition();
        bool seeked = _isPlaying &&
                      (position - interpolated).Duration() > TimeSpan.FromMilliseconds(1200);

        if (!_isPlaying || seeked || _anchorPosition == TimeSpan.Zero)
        {
            _anchorPosition = position;
            _positionClock.Restart();
        }

        _duration = duration;
        _isPlaying = playing;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private GlobalSystemMediaTransportControlsSession? GetActiveSession()
    {
        if (_session != null) return _session;
        if (_manager == null) return null;
        return PickSession(_manager);
    }

    private GlobalSystemMediaTransportControlsSession? PickSession(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        GlobalSystemMediaTransportControlsSession? best = null;
        int bestScore = -1;

        try
        {
            var current = manager.GetCurrentSession();
            foreach (var session in manager.GetSessions())
            {
                int score;
                try
                {
                    score = MapState(session.GetPlaybackInfo()?.PlaybackStatus) switch
                    {
                        PlaybackState.Playing => 40,
                        PlaybackState.Paused => 30,
                        PlaybackState.Stopped => 20,
                        _ => 5
                    };

                    var appId = session.SourceAppUserModelId ?? string.Empty;
                    if (appId.Contains("QQMusic", StringComparison.OrdinalIgnoreCase))
                        score += 6;
                    if (ReferenceEquals(session, current))
                        score += 8;
                }
                catch
                {
                    score = 0;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = session;
                }
            }
        }
        catch { }

        return best;
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> GetManagerAsync()
    {
        if (_manager != null) return _manager;

        if ((DateTime.UtcNow - _lastManagerAttempt).TotalMilliseconds < 2000)
            return null;
        _lastManagerAttempt = DateTime.UtcNow;

        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (manager != null)
            {
                manager.SessionsChanged += OnSessionsChanged;
                _manager = manager;
            }
            return manager;
        }
        catch
        {
            _manager = null;
            return null;
        }
    }

    private static async Task<byte[]?> TryReadThumbnailAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties? media)
    {
        if (media?.Thumbnail == null) return null;

        try
        {
            using var stream = await media.Thumbnail.OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.AsStream().CopyToAsync(memory);
            return memory.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private void SetClosed()
    {
        if (_track.State == PlaybackState.Closed && _track.IsEmpty) return;

        DetachSession();
        _sessionId = string.Empty;
        _trackKey = string.Empty;
        _anchorPosition = TimeSpan.Zero;
        _duration = TimeSpan.Zero;
        _isPlaying = false;

        var closed = new TrackInfo
        {
            State = PlaybackState.Closed,
            SourceName = "未连接"
        };

        StoreTrack(closed);
        TrackChanged?.Invoke(closed);
        PlaybackStateChanged?.Invoke(PlaybackState.Closed);
    }

    private void StoreTrack(TrackInfo track)
    {
        lock (_gate)
        {
            _track = Snapshot(track);
        }
    }

    private TrackInfo Snapshot(TrackInfo track) => new()
    {
        Title = track.Title,
        Artist = track.Artist,
        Album = track.Album,
        Year = track.Year,
        Duration = track.Duration,
        Position = GetPosition(),
        State = track.State,
        ThumbnailBytes = track.ThumbnailBytes,
        SourceAppId = track.SourceAppId,
        SourceName = track.SourceName,
        IsShuffleActive = track.IsShuffleActive,
        SupportsShuffle = track.SupportsShuffle,
        SourceKind = "SMTC",
        LyricSource = track.LyricSource,
        Lyrics = track.Lyrics
    };

    private void AttachSession(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
        catch { }
    }

    private void DetachSession()
    {
        if (_session == null) return;

        try
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        catch { }

        _session = null;
    }

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args) => _ = RefreshAsync(forceMedia: true);

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) => _ = RefreshAsync(forceMedia: true);

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) => _ = RefreshAsync();

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args) => _ = RefreshAsync();

    private static PlaybackState MapState(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus? status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackState.Stopped,
        _ => PlaybackState.Closed
    };

    private static string GetSourceName(string appId)
    {
        if (appId.Contains("QQMusic", StringComparison.OrdinalIgnoreCase) ||
            appId.Contains("QQ音乐", StringComparison.OrdinalIgnoreCase))
            return "QQ音乐";
        if (appId.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
            return "Spotify";
        if (appId.Contains("CloudMusic", StringComparison.OrdinalIgnoreCase) ||
            appId.Contains("Netease", StringComparison.OrdinalIgnoreCase))
            return "网易云音乐";
        if (appId.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase) ||
            appId.Contains("Apple.Music", StringComparison.OrdinalIgnoreCase))
            return "Apple Music";
        if (appId.Contains("ZuneMusic", StringComparison.OrdinalIgnoreCase) ||
            appId.Contains("MediaPlayer", StringComparison.OrdinalIgnoreCase))
            return "媒体播放器";

        return "系统媒体";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _positionTimer.Stop();
        _fallbackTimer.Stop();
        _refreshLock.Dispose();
    }
}
