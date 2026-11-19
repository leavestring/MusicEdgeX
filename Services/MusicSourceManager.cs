using System.Windows.Threading;
using MusicEdge.Models;

namespace MusicEdge.Services;

public sealed class MusicSourceManager : IDisposable
{
    public event Action<TrackInfo>? TrackChanged;
    public event Action<TimeSpan>? PositionChanged;
    public event Action<PlayerCapConnection>? SourceStatusChanged;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly SMTCMusicService _smtc = new();
    private readonly DispatcherTimer _ticker = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(200)
    };
    private AppSettings _settings;
    private PlayerCapService? _cap;
    private TrackInfo _current = new();
    private TimeSpan _lastPublishedPosition = TimeSpan.MinValue;
    private bool _panelVisible;
    private bool _running;

    public MusicSourceManager(AppSettings settings)
    {
        _settings = settings.Clone();
        _smtc.Changed += Refresh;
        _ticker.Tick += (_, _) => PublishPosition();
    }

    public PlayerCapConnection Connection { get; private set; }
    public TimeSpan CurrentPosition => _current.IsEmpty ? TimeSpan.Zero :
        _current.HasMediaSession && (_smtc.HasTimeline || _current.LyricSource.Length == 0)
            ? _smtc.GetPosition() : _cap?.GetPosition() ?? TimeSpan.Zero;

    public bool IsPanelVisible
    {
        get => _panelVisible;
        set
        {
            if (_panelVisible == value) return;
            _panelVisible = value;
            UpdateTicker();
            if (value) PublishPosition(force: true);
        }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _smtc.Start();
        RebuildBridge();
    }

    public void UpdateSettings(AppSettings settings)
    {
        bool rebuild = _settings.UsePlayerCap != settings.UsePlayerCap ||
                       !string.Equals(_settings.PlayerCapHost, settings.PlayerCapHost, StringComparison.OrdinalIgnoreCase) ||
                       !string.Equals(_settings.PlayerCapPlayer, settings.PlayerCapPlayer, StringComparison.OrdinalIgnoreCase);
        _settings = settings.Clone();
        if (_running && rebuild)
            RebuildBridge();
    }

    public Task<bool> TogglePlayPauseAsync() => _smtc.TogglePlayPauseAsync(CommandTarget);
    public Task<bool> SkipNextAsync() => _smtc.SkipNextAsync(CommandTarget);
    public Task<bool> SkipPreviousAsync() => _smtc.SkipPreviousAsync(CommandTarget);
    public Task<bool> SeekAsync(TimeSpan position) => _smtc.SeekAsync(CommandTarget, position);
    public Task<bool> SetShuffleAsync(bool active) => _smtc.SetShuffleAsync(CommandTarget, active);

    private string CommandTarget => _current.HasMediaSession ? _current.SourceAppId : string.Empty;

    private void RebuildBridge()
    {
        _cap?.Dispose();
        _cap = null;
        if (_settings.UsePlayerCap)
        {
            var bridge = new PlayerCapService(_settings.PlayerCapHost, _settings.PlayerCapPlayer);
            int pending = 0;
            bridge.Changed += () =>
            {
                if (Interlocked.Exchange(ref pending, 1) != 0) return;
                _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    Interlocked.Exchange(ref pending, 0);
                    if (ReferenceEquals(bridge, _cap)) Refresh();
                }));
            };
            _cap = bridge;
            bridge.Start();
        }

        Refresh();
    }

    private void Refresh()
    {
        if (!_running) return;
        var cap = _cap?.GetSnapshot();
        var connection = cap?.Connection ?? PlayerCapConnection.Idle;
        bool connectionChanged = Connection != connection;
        Connection = connection;
        var snapshot = SelectTrack(_smtc.CurrentTrack,
            connection == PlayerCapConnection.Connected ? cap?.Track : null,
            _smtc.HasMaterialMusicSession, _smtc.HasOtherPlayback);

        // Position updates do not invalidate the metadata or lyric controls.
        if (_current != (snapshot with { Position = TimeSpan.Zero }))
        {
            _current = snapshot with { Position = TimeSpan.Zero };
            TrackChanged?.Invoke(snapshot);
        }
        if (connectionChanged)
            SourceStatusChanged?.Invoke(connection);

        UpdateTicker();
        if (_panelVisible) PublishPosition();
    }

    internal static TrackInfo SelectTrack(TrackInfo system, TrackInfo? bridge,
        bool hasMusicSession, bool otherMediaPlaying)
    {
        bool hasBridgeTrack = bridge != null && !bridge.IsEmpty && MusicPlayers.FromBridge(bridge.SourceAppId) != null &&
                              bridge.State is PlaybackState.Playing or PlaybackState.Paused;
        if (!system.IsEmpty && system.HasMediaSession && MusicPlayers.FromAppId(system.SourceAppId) != null &&
            system.State is PlaybackState.Playing or PlaybackState.Paused)
        {
            if (system.State == PlaybackState.Paused && otherMediaPlaying) return new TrackInfo();
            if (hasBridgeTrack && MusicPlayers.SameSong(system, bridge!))
            {
                return system with
                {
                    ThumbnailBytes = bridge!.ThumbnailBytes ?? system.ThumbnailBytes,
                    Duration = system.Duration > TimeSpan.Zero ? system.Duration : bridge.Duration,
                    Position = system.Duration > TimeSpan.Zero ? system.Position : bridge.Position,
                    Controls = system.Duration > TimeSpan.Zero ? system.Controls : system.Controls & ~PlaybackControls.Seek,
                    Lyrics = bridge.Lyrics,
                    LyricSource = bridge.LyricSource
                };
            }
            return system;
        }

        if (!hasMusicSession && hasBridgeTrack && (!otherMediaPlaying || bridge!.State == PlaybackState.Playing))
            return bridge!;

        return new TrackInfo();
    }

    private void UpdateTicker()
    {
        bool shouldTick = _running && _panelVisible && !_current.IsEmpty && _current.State == PlaybackState.Playing;
        if (shouldTick != _ticker.IsEnabled)
            _ticker.IsEnabled = shouldTick;
    }

    private void PublishPosition(bool force = false)
    {
        var position = CurrentPosition;
        if (_current.Duration > TimeSpan.Zero && position > _current.Duration)
            position = _current.Duration;
        if (!force && position == _lastPublishedPosition) return;
        _lastPublishedPosition = position;
        PositionChanged?.Invoke(position);
    }

    public void Dispose()
    {
        _running = false;
        _ticker.Stop();
        var bridge = _cap;
        _cap = null;
        bridge?.Dispose();
        _smtc.Dispose();
    }
}
