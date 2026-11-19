using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Windows.Media.Control;
using MusicEdge.Models;

namespace MusicEdge.Services;

public sealed class SMTCMusicService : IDisposable
{
    private const int RefreshSession = 1;
    private const int RefreshMedia = 2;
    private const int RefreshTimeline = 4;
    private const long MaxThumbnailBytes = 8 * 1024 * 1024;

    public event Action? Changed;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _fallback = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(8)
    };
    private readonly HashSet<GlobalSystemMediaTransportControlsSession> _observed = [];
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private TrackInfo _track = new();
    private TimeSpan _anchorPosition;
    private long _anchorTimestamp;
    private long _artVersion;
    private long _seekTimestamp;
    private long _lastTimelineTimestamp = long.MinValue;
    private TimeSpan _lastTimelinePosition;
    private int _pending;
    private int _refreshScheduled;
    private volatile bool _running;

    public SMTCMusicService() =>
        _fallback.Tick += (_, _) => RequestRefresh(RefreshSession | RefreshMedia);

    public TrackInfo CurrentTrack => _track with { Position = GetPosition() };
    public bool HasTimeline => _track.Duration > TimeSpan.Zero;
    public bool HasMaterialMusicSession { get; private set; }
    public bool HasOtherPlayback { get; private set; }

    public TimeSpan GetPosition()
    {
        var position = _anchorPosition;
        if (_track.State == PlaybackState.Playing && _anchorTimestamp != 0)
            position += Stopwatch.GetElapsedTime(_anchorTimestamp);
        return Clamp(position, TimeSpan.Zero,
            _track.Duration > TimeSpan.Zero ? _track.Duration : TimeSpan.MaxValue);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _fallback.Start();
        RequestRefresh(RefreshSession | RefreshMedia);
    }

    private void RequestRefresh(int reason)
    {
        if (!_running) return;
        Interlocked.Or(ref _pending, reason);
        if (Interlocked.Exchange(ref _refreshScheduled, 1) == 0)
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DrainRefreshes));
    }

    private async void DrainRefreshes()
    {
        try
        {
            int pending;
            while (_running && (pending = Interlocked.Exchange(ref _pending, 0)) != 0)
            {
                try { await RefreshAsync(pending); }
                catch (Exception ex) { Debug.WriteLine(ex); }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshScheduled, 0);
            if (_running && Volatile.Read(ref _pending) != 0)
                RequestRefresh(0);
        }
    }

    private async Task RefreshAsync(int reason)
    {
        if (_manager == null)
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (!_running || manager == null) return;
            _manager = manager;
            _manager.SessionsChanged += OnSessionsChanged;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        }

        var session = _session;
        GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback;
        bool selectionChanged = false;
        bool contextChanged = false;
        if ((reason & RefreshSession) != 0)
        {
            var current = _manager.GetCurrentSession();
            var sessions = _manager.GetSessions();
            ObserveSessions(sessions);
            (session, playback, var hasMusic, var otherPlaying) = PickSession(sessions, current);
            contextChanged = HasMaterialMusicSession != hasMusic || HasOtherPlayback != otherPlaying;
            HasMaterialMusicSession = hasMusic;
            HasOtherPlayback = otherPlaying;

            selectionChanged = !Equals(session, _session);
            if (selectionChanged)
            {
                DetachSelectedSession();
                _session = session;
                if (session != null)
                {
                    session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                }
            }
        }
        else
        {
            playback = ReadPlayback(session);
        }

        if (session == null)
        {
            if (!_track.IsEmpty || contextChanged)
            {
                ClearTrack();
                Changed?.Invoke();
            }
            return;
        }

        if (selectionChanged)
        {
            ClearTrack();
            Changed?.Invoke();
        }

        var state = MapState(playback?.PlaybackStatus);
        if (state is not (PlaybackState.Playing or PlaybackState.Paused))
        {
            RequestRefresh(RefreshSession);
            return;
        }

        var previousPosition = GetPosition();
        var position = previousPosition;
        var duration = _track.Duration;
        var timelineTimestamp = _lastTimelineTimestamp;
        var timelinePosition = _lastTimelinePosition;
        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline != null)
            {
                timelineTimestamp = timeline.LastUpdatedTime.UtcTicks;
                timelinePosition = timeline.Position;
                duration = timeline.EndTime > timeline.StartTime
                    ? timeline.EndTime - timeline.StartTime
                    : TimeSpan.Zero;
                var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                var supplied = timeline.Position;
                if (state == PlaybackState.Playing && elapsed > TimeSpan.Zero &&
                    elapsed < TimeSpan.FromSeconds(15) && timelineTimestamp != _lastTimelineTimestamp)
                    supplied += elapsed;
                if (selectionChanged || timelineTimestamp != _lastTimelineTimestamp ||
                    timelinePosition != _lastTimelinePosition ||
                    (_seekTimestamp == 0 && _track.State != state))
                    position = supplied;
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }

        bool readMedia = selectionChanged || _track.IsEmpty || (reason & RefreshMedia) != 0;
        GlobalSystemMediaTransportControlsSessionMediaProperties? media = null;
        if (readMedia)
        {
            try { media = await session.TryGetMediaPropertiesAsync(); }
            catch (Exception ex) { Debug.WriteLine(ex); }
            if (!_running) return;

            // If selection or media changed during the read, let the queued pass
            // resolve it before exposing metadata or enabling commands.
            if ((Volatile.Read(ref _pending) & (RefreshSession | RefreshMedia)) != 0)
            {
                Interlocked.Or(ref _pending, RefreshMedia);
                return;
            }
        }

        var next = _track;
        bool songChanged = false;
        if (media != null)
        {
            string title = media.Title ?? string.Empty;
            string artist = media.Artist ?? string.Empty;
            string album = media.AlbumTitle ?? string.Empty;
            songChanged = _track.Title != title || _track.Artist != artist || _track.Album != album;
            if (songChanged)
            {
                next = next with { Title = title, Artist = artist, Album = album, ThumbnailBytes = null };
                _seekTimestamp = 0;
                if (!selectionChanged && timelineTimestamp == _lastTimelineTimestamp &&
                    timelinePosition == _lastTimelinePosition && position > TimeSpan.FromSeconds(2))
                {
                    position = TimeSpan.Zero;
                    duration = TimeSpan.Zero;
                }
            }
        }

        string sourceId = session.SourceAppUserModelId ?? string.Empty;
        next = next with
        {
            State = state,
            Duration = duration,
            SourceAppId = sourceId,
            SourceName = MusicPlayers.DisplayName(MusicPlayers.FromAppId(sourceId)),
            HasMediaSession = true,
            Controls = GetControls(playback, state),
            IsShuffleActive = playback?.IsShuffleActive ?? false
        };

        if (_seekTimestamp != 0)
        {
            // A few players echo a pre-seek timeline just after accepting a seek.
            bool fresh = (timelineTimestamp != _lastTimelineTimestamp || timelinePosition != _lastTimelinePosition) &&
                         (position - previousPosition).Duration() < TimeSpan.FromSeconds(2);
            if (fresh || Stopwatch.GetElapsedTime(_seekTimestamp) >= TimeSpan.FromSeconds(2))
                _seekTimestamp = 0;
            else
                position = previousPosition;
        }

        if (songChanged || selectionChanged || position != previousPosition || state != _track.State)
        {
            _anchorPosition = Clamp(position, TimeSpan.Zero,
                duration > TimeSpan.Zero ? duration : TimeSpan.MaxValue);
            _anchorTimestamp = Stopwatch.GetTimestamp();
        }

        if (_seekTimestamp == 0)
        {
            _lastTimelineTimestamp = timelineTimestamp;
            _lastTimelinePosition = timelinePosition;
        }
        if (contextChanged || next != _track ||
            (state == PlaybackState.Paused && position != previousPosition))
        {
            _track = next;
            Changed?.Invoke();
        }

        if (songChanged) ++_artVersion;
        if (media?.Thumbnail != null && _track.ThumbnailBytes == null)
            _ = ReadArtworkAsync(media, ++_artVersion);
    }

    private static (GlobalSystemMediaTransportControlsSession? Session,
        GlobalSystemMediaTransportControlsSessionPlaybackInfo? Playback,
        bool HasMusic, bool OtherPlaying) PickSession(
        IEnumerable<GlobalSystemMediaTransportControlsSession> sessions,
        GlobalSystemMediaTransportControlsSession? current)
    {
        GlobalSystemMediaTransportControlsSession? best = null;
        GlobalSystemMediaTransportControlsSessionPlaybackInfo? bestPlayback = null;
        int priority = 0;
        bool otherPlaying = false;
        foreach (var session in sessions)
        {
            var playback = ReadPlayback(session);
            var state = MapState(playback?.PlaybackStatus);
            var player = MusicPlayers.FromAppId(session.SourceAppUserModelId ?? string.Empty);
            if (player == null && state == PlaybackState.Playing)
                otherPlaying = true;

            int score = MusicPlayers.SessionPriority(player, state, Equals(session, current));
            if (score <= priority) continue;
            priority = score;
            best = session;
            bestPlayback = playback;
        }

        return priority < 3 && otherPlaying
            ? (null, null, priority != 0, true)
            : (best, bestPlayback, priority != 0, otherPlaying);
    }

    private void ObserveSessions(IEnumerable<GlobalSystemMediaTransportControlsSession> sessions)
    {
        var active = sessions.ToHashSet();
        foreach (var removed in _observed.Where(session => !active.Contains(session)).ToArray())
        {
            try { removed.PlaybackInfoChanged -= OnPlaybackInfoChanged; }
            catch (Exception ex) { Debug.WriteLine(ex); }
            _observed.Remove(removed);
        }
        foreach (var session in active)
        {
            if (_observed.Add(session))
                session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }
    }

    private void DetachSelectedSession()
    {
        if (_session == null) return;
        var session = _session;
        _session = null;
        try
        {
            session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void ClearTrack()
    {
        _track = new TrackInfo();
        _anchorPosition = TimeSpan.Zero;
        _anchorTimestamp = 0;
        _seekTimestamp = 0;
        _lastTimelineTimestamp = long.MinValue;
        _lastTimelinePosition = TimeSpan.Zero;
        ++_artVersion;
    }

    private async Task ReadArtworkAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties media, long version)
    {
        if (media.Thumbnail == null) return;
        try
        {
            using var stream = await media.Thumbnail.OpenReadAsync();
            if (stream.Size is 0 or > MaxThumbnailBytes || !_running || version != _artVersion) return;
            var bytes = new byte[(int)stream.Size];
            using var reader = stream.AsStream();
            await reader.ReadExactlyAsync(bytes);
            if (!_running || version != _artVersion) return;
            _track = _track with { ThumbnailBytes = bytes };
            Changed?.Invoke();
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    public Task<bool> TogglePlayPauseAsync(string appId) => ExecuteAsync(appId, PlaybackControls.PlayPause,
        async session =>
        {
            var playback = ReadPlayback(session);
            var controls = playback?.Controls;
            if (playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                return controls?.IsPauseEnabled == true
                    ? await session.TryPauseAsync()
                    : controls?.IsPlayPauseToggleEnabled == true && await session.TryTogglePlayPauseAsync();
            return controls?.IsPlayEnabled == true
                ? await session.TryPlayAsync()
                : controls?.IsPlayPauseToggleEnabled == true && await session.TryTogglePlayPauseAsync();
        });

    public Task<bool> SkipNextAsync(string appId) => ExecuteAsync(appId, PlaybackControls.Next,
        async session => await session.TrySkipNextAsync(), refreshMedia: true);

    public Task<bool> SkipPreviousAsync(string appId) => ExecuteAsync(appId, PlaybackControls.Previous,
        async session => await session.TrySkipPreviousAsync(), refreshMedia: true);

    public Task<bool> SetShuffleAsync(string appId, bool active) => ExecuteAsync(appId, PlaybackControls.Shuffle,
        async session => await session.TryChangeShuffleActiveAsync(active));

    public async Task<bool> SeekAsync(string appId, TimeSpan position)
    {
        var session = GetCommandSession(appId, PlaybackControls.Seek);
        if (session == null) return false;
        var track = _track;
        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline != null && timeline.MaxSeekTime > timeline.MinSeekTime)
                position = Clamp(position, timeline.MinSeekTime, timeline.MaxSeekTime);
            if (!await session.TryChangePlaybackPositionAsync(position.Ticks)) return false;

            if (Equals(session, _session) && track.Title == _track.Title && track.Artist == _track.Artist)
            {
                _anchorPosition = position;
                _anchorTimestamp = _seekTimestamp = Stopwatch.GetTimestamp();
                Changed?.Invoke();
            }
            RequestRefresh(RefreshTimeline);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    private GlobalSystemMediaTransportControlsSession? GetCommandSession(string appId, PlaybackControls control) =>
        _running && !_track.IsEmpty && _track.SourceAppId == appId && _track.CanControl(control)
            ? _session
            : null;

    private async Task<bool> ExecuteAsync(string appId, PlaybackControls control,
        Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command, bool refreshMedia = false)
    {
        var session = GetCommandSession(appId, control);
        if (session == null) return false;
        try
        {
            if (!await command(session)) return false;
            RequestRefresh(RefreshSession | (refreshMedia ? RefreshMedia : 0));
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) =>
        RequestRefresh(RefreshSession);

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        RequestRefresh(RefreshSession);

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        RequestRefresh(RefreshMedia);

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        RequestRefresh(RefreshSession);

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        RequestRefresh(RefreshTimeline);

    private static GlobalSystemMediaTransportControlsSessionPlaybackInfo? ReadPlayback(
        GlobalSystemMediaTransportControlsSession? session)
    {
        try { return session?.GetPlaybackInfo(); }
        catch (Exception ex) { Debug.WriteLine(ex); return null; }
    }

    private static PlaybackState MapState(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackState.Stopped,
        _ => PlaybackState.Closed
    };

    private static PlaybackControls GetControls(
        GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback, PlaybackState state)
    {
        var controls = playback?.Controls;
        if (controls == null) return PlaybackControls.None;
        var result = PlaybackControls.None;
        if (controls.IsPlayPauseToggleEnabled ||
            (state == PlaybackState.Playing ? controls.IsPauseEnabled : controls.IsPlayEnabled))
            result |= PlaybackControls.PlayPause;
        if (controls.IsNextEnabled) result |= PlaybackControls.Next;
        if (controls.IsPreviousEnabled) result |= PlaybackControls.Previous;
        if (controls.IsPlaybackPositionEnabled) result |= PlaybackControls.Seek;
        if (controls.IsShuffleEnabled) result |= PlaybackControls.Shuffle;
        return result;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    public void Dispose()
    {
        _running = false;
        _fallback.Stop();
        DetachSelectedSession();
        foreach (var session in _observed)
        {
            try { session.PlaybackInfoChanged -= OnPlaybackInfoChanged; }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
        _observed.Clear();
        if (_manager != null)
        {
            try
            {
                _manager.SessionsChanged -= OnSessionsChanged;
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
            _manager = null;
        }
    }
}
