using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MusicEdge.Models;

namespace MusicEdge.Services;

public enum PlayerCapConnection
{
    Idle,
    Probing,
    Connecting,
    Connected,
    Disconnected,
    Unreachable
}

/// <summary>
/// Optional richer source: consumes the PlayerCap local bridge
/// (GET /service-status + WebSocket /ws). It supplies track metadata, cover art,
/// precise playback position and timed / word-level lyrics that SMTC cannot expose.
/// Control commands intentionally stay on the SMTC/media-key path.
/// </summary>
public sealed class PlayerCapService : IDisposable
{
    private const string DefaultHost = "127.0.0.1:8765";
    private const int ReconnectDelayMs = 2200;
    private const int ReconnectProbeDelayMs = 3200;

    public event Action<TrackInfo>? TrackChanged;
    public event Action<PlaybackState>? PlaybackStateChanged;
    public event Action<IReadOnlyList<LyricLine>>? LyricsChanged;
    public event Action<PlayerCapConnection, string>? ConnectionChanged;

    private readonly object _gate = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2.5) };
    private readonly SemaphoreSlim _coverLock = new(1, 1);
    private readonly string _host;
    private readonly string _player;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _running;
    private bool _disposed;

    private string _activePlayer = string.Empty;
    private string _statusDetail = string.Empty;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string _trackKey = string.Empty;
    private string _coverKey = string.Empty;
    private byte[]? _coverBytes;
    private string _currentLyricText = string.Empty;
    private string _currentTranslation = string.Empty;
    private List<LyricLine> _lyrics = new();

    private TimeSpan _duration;
    private double _positionSec;
    private DateTime _anchorUtc = DateTime.UtcNow;
    private bool _playing;
    private PlaybackState _state = PlaybackState.Closed;
    private PlayerCapConnection _connection = PlayerCapConnection.Idle;
    private IReadOnlyList<string> _supportedPlayers = Array.Empty<string>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public PlayerCapService(string? host = null, string? player = null)
    {
        _host = NormalizeHost(host);
        _player = player?.Trim() ?? string.Empty;
    }

    public PlayerCapConnection Connection
    {
        get { lock (_gate) return _connection; }
    }

    public bool IsConnected => Connection == PlayerCapConnection.Connected;

    public string ActivePlayer
    {
        get { lock (_gate) return _activePlayer; }
    }

    public string StatusDetail
    {
        get { lock (_gate) return _statusDetail; }
    }

    public IReadOnlyList<string> SupportedPlayers
    {
        get { lock (_gate) return _supportedPlayers; }
    }

    public bool HasTrack
    {
        get { lock (_gate) return !string.IsNullOrWhiteSpace(_title) || !string.IsNullOrWhiteSpace(_artist); }
    }

    public TrackInfo CurrentTrack
    {
        get
        {
            lock (_gate)
            {
                return BuildSnapshotLocked();
            }
        }
    }

    public TimeSpan GetPosition()
    {
        lock (_gate)
        {
            return TimeSpan.FromSeconds(GetPositionLocked());
        }
    }

    public void Start()
    {
        if (_running || _disposed) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        SetConnection(PlayerCapConnection.Idle, string.Empty);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SetConnection(PlayerCapConnection.Probing, string.Empty);
            var service = await ProbeAsync(cancellationToken);
            if (service == null)
            {
                SetConnection(PlayerCapConnection.Unreachable, "未发现 PlayerCap 服务");
                if (!await DelayAsync(ReconnectProbeDelayMs, cancellationToken)) return;
                continue;
            }

            lock (_gate)
            {
                _supportedPlayers = service.PlayerSupport ?? new List<string>();
            }

            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            SetConnection(PlayerCapConnection.Connecting, "正在连接 PlayerCap");

            try
            {
                var path = string.IsNullOrEmpty(_player) ? "/ws" : $"/{Uri.EscapeDataString(_player)}/ws";
                await socket.ConnectAsync(new Uri($"ws://{_host}{path}"), cancellationToken);
                SetConnection(PlayerCapConnection.Connected, "PlayerCap 已连接");
                await ReceiveLoopAsync(socket, service, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                SetConnection(PlayerCapConnection.Disconnected, "PlayerCap 连接中断");
            }

            if (cancellationToken.IsCancellationRequested) return;
            if (!await DelayAsync(ReconnectDelayMs, cancellationToken)) return;
        }
    }

    private async Task<ServiceStatus?> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await _http.GetStringAsync($"http://{_host}/service-status", cancellationToken);
            var envelope = JsonSerializer.Deserialize<ServiceEnvelope>(json, JsonOptions);
            return envelope?.Data;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        ServiceStatus service,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var builder = new StringBuilder();

        if (!string.IsNullOrEmpty(_player))
        {
            lock (_gate)
            {
                _activePlayer = _player;
            }
        }
        else if (service.PlayerRunning is { Count: > 0 })
        {
            lock (_gate)
            {
                _activePlayer = service.PlayerRunning[0];
            }
        }

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return;

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;

            var json = builder.ToString();
            builder.Clear();
            HandleMessage(json);
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return;

            string? type = typeElement.GetString();
            string? player = root.TryGetProperty("player", out var playerElement)
                ? playerElement.GetString()
                : null;
            var data = root.TryGetProperty("data", out var dataElement) ? dataElement : default;

            if (!string.IsNullOrEmpty(player))
            {
                lock (_gate)
                {
                    _activePlayer = player;
                }
            }

            switch (type)
            {
                case "status_update":
                    HandleStatus(Read<StatusData>(data));
                    break;
                case "song_info_update":
                    HandleSongInfo(Read<SongInfoData>(data));
                    break;
                case "all_lyrics":
                    HandleAllLyrics(Read<AllLyricsData>(data));
                    break;
                case "lyric_update":
                    HandleLyricUpdate(Read<LyricUpdateData>(data));
                    break;
                case "playback_pause":
                case "playback_resume":
                    HandlePlayback(Read<PlaybackData>(data), type == "playback_resume");
                    break;
                case "player_switch":
                    HandlePlayerSwitch(Read<PlayerSwitchData>(data));
                    break;
                case "player_clear":
                    ClearContent();
                    break;
                case "lyric_idle":
                    ClearLyrics();
                    break;
            }
        }
        catch
        {
            // Malformed or unknown frames must not kill the receive loop.
        }
    }

    private void HandleStatus(StatusData? data)
    {
        if (data == null) return;

        PlaybackState state;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            _positionSec = GetPositionLocked(now);
            _anchorUtc = now;
            _statusDetail = data.Detail ?? string.Empty;
            state = MapState(data.Status);
            _state = state;
            _playing = state == PlaybackState.Playing;
        }

        PlaybackStateChanged?.Invoke(state);
        TrackChanged?.Invoke(CurrentTrack);
    }

    private void HandleSongInfo(SongInfoData? data)
    {
        if (data == null) return;

        string title = data.Name ?? data.Title ?? string.Empty;
        string artist = data.Singer ?? string.Empty;
        string key = $"{_activePlayer}\u001f{title}\u001f{artist}";

        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(key, _trackKey, StringComparison.Ordinal);
            if (changed)
            {
                _trackKey = key;
                _title = title;
                _artist = artist;
                _lyrics = new List<LyricLine>();
                _currentLyricText = string.Empty;
                _currentTranslation = string.Empty;
                _coverBytes = null;
            }
        }

        if (!changed) return;

        TrackChanged?.Invoke(CurrentTrack);
        LyricsChanged?.Invoke(Array.Empty<LyricLine>());

        var coverBase64 = data.CoverBase64 ?? string.Empty;
        var coverUrl = data.Cover ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(coverBase64) || !string.IsNullOrWhiteSpace(coverUrl))
            _ = LoadCoverAsync(coverBase64, coverUrl, key);
    }

    private void HandleAllLyrics(AllLyricsData? data)
    {
        if (data == null) return;

        var lyrics = MapLyrics(data);
        lock (_gate)
        {
            _duration = TimeSpan.FromSeconds(Math.Max(0, data.Duration));
            if (double.IsFinite(data.Position) && data.Position >= 0)
            {
                _positionSec = data.Position;
                _anchorUtc = DateTime.UtcNow;
            }
            _lyrics = lyrics;
            if (lyrics.Count > 0)
            {
                _currentLyricText = lyrics[0].Text;
                _currentTranslation = lyrics[0].Translation;
            }
        }

        LyricsChanged?.Invoke(lyrics);
        TrackChanged?.Invoke(CurrentTrack);
    }

    private void HandleLyricUpdate(LyricUpdateData? data)
    {
        if (data == null) return;

        bool changed;
        lock (_gate)
        {
            if (double.IsFinite(data.Position) && data.Position >= 0)
            {
                _positionSec = data.Position;
                _anchorUtc = DateTime.UtcNow;
            }

            var text = data.Text ?? string.Empty;
            var translation = data.SubText ?? string.Empty;
            changed = !string.Equals(text, _currentLyricText, StringComparison.Ordinal) ||
                      !string.Equals(translation, _currentTranslation, StringComparison.Ordinal);
            _currentLyricText = text;
            _currentTranslation = translation;

            if (_lyrics.Count == 0 && !string.IsNullOrWhiteSpace(text))
            {
                var start = TimeSpan.FromSeconds(Math.Max(0, data.PlayTime > 0 ? data.PlayTime : data.Position));
                var end = _duration > start ? _duration : start + TimeSpan.FromSeconds(6);
                _lyrics = new List<LyricLine>
                {
                    new()
                    {
                        Start = start,
                        End = end,
                        Text = text,
                        Translation = translation
                    }
                };
                changed = true;
            }
        }

        if (changed)
            LyricsChanged?.Invoke(_lyrics.ToArray());
    }

    private void HandlePlayback(PlaybackData? data, bool playing)
    {
        lock (_gate)
        {
            if (data != null && double.IsFinite(data.Position) && data.Position >= 0)
                _positionSec = data.Position;
            else
                _positionSec = GetPositionLocked();
            _anchorUtc = DateTime.UtcNow;
            _playing = playing;
            _state = playing ? PlaybackState.Playing : PlaybackState.Paused;
        }

        PlaybackStateChanged?.Invoke(playing ? PlaybackState.Playing : PlaybackState.Paused);
    }

    private void HandlePlayerSwitch(PlayerSwitchData? data)
    {
        var target = data?.To ?? string.Empty;
        if (string.IsNullOrEmpty(target))
        {
            ClearContent();
            return;
        }

        lock (_gate)
        {
            _activePlayer = target;
            ClearContentLocked();
        }

        TrackChanged?.Invoke(CurrentTrack);
        LyricsChanged?.Invoke(Array.Empty<LyricLine>());
    }

    private void ClearContent()
    {
        lock (_gate)
        {
            ClearContentLocked();
        }
        TrackChanged?.Invoke(CurrentTrack);
        LyricsChanged?.Invoke(Array.Empty<LyricLine>());
        PlaybackStateChanged?.Invoke(PlaybackState.Closed);
    }

    private void ClearContentLocked()
    {
        _title = string.Empty;
        _artist = string.Empty;
        _trackKey = string.Empty;
        _coverKey = string.Empty;
        _coverBytes = null;
        _lyrics = new List<LyricLine>();
        _currentLyricText = string.Empty;
        _currentTranslation = string.Empty;
        _duration = TimeSpan.Zero;
        _positionSec = 0;
        _anchorUtc = DateTime.UtcNow;
        _playing = false;
        _state = PlaybackState.Closed;
        _statusDetail = string.Empty;
    }

    private void ClearLyrics()
    {
        lock (_gate)
        {
            _lyrics = new List<LyricLine>();
            _currentLyricText = string.Empty;
            _currentTranslation = string.Empty;
        }
        LyricsChanged?.Invoke(Array.Empty<LyricLine>());
    }

    private async Task LoadCoverAsync(string base64, string url, string key)
    {
        await _coverLock.WaitAsync();
        try
        {
            byte[]? bytes = null;

            if (!string.IsNullOrWhiteSpace(base64))
            {
                var payload = base64;
                if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = payload.IndexOf(',');
                    if (comma >= 0) payload = payload[(comma + 1)..];
                }
                try { bytes = Convert.FromBase64String(payload); } catch { }
            }

            if (bytes == null && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                try { bytes = await _http.GetByteArrayAsync(uri); } catch { }
            }

            if (bytes == null) return;

            lock (_gate)
            {
                if (!string.Equals(key, _trackKey, StringComparison.Ordinal)) return;
                _coverKey = key;
                _coverBytes = bytes;
            }

            TrackChanged?.Invoke(CurrentTrack);
        }
        finally
        {
            _coverLock.Release();
        }
    }

    private static List<LyricLine> MapLyrics(AllLyricsData data)
    {
        if (data.Count <= 0 || data.Lyrics == null) return new List<LyricLine>();

        var raw = data.Lyrics
            .Where(line => line.Index >= 0 && !string.IsNullOrWhiteSpace(line.Text))
            .Select(line => new
            {
                Line = line,
                Start = line.PlayTime > 0 ? line.PlayTime : line.Timestamp
            })
            .OrderBy(item => item.Start)
            .ToList();

        var result = new List<LyricLine>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            double start = Math.Max(0, raw[i].Start);
            double next = i + 1 < raw.Count ? Math.Max(start, raw[i + 1].Start) : 0;
            double end = next > start ? next : (data.Duration > start ? data.Duration : start + 6);

            result.Add(new LyricLine
            {
                Start = TimeSpan.FromSeconds(start),
                End = TimeSpan.FromSeconds(end),
                Text = raw[i].Line.Text?.Trim() ?? string.Empty,
                Translation = raw[i].Line.SubText?.Trim() ?? string.Empty
            });
        }

        return result;
    }

    private TrackInfo BuildSnapshotLocked() => new()
    {
        Title = _title,
        Artist = _artist,
        Album = string.Empty,
        Duration = _duration,
        Position = TimeSpan.FromSeconds(GetPositionLocked()),
        State = _state,
        ThumbnailBytes = _coverBytes,
        SourceAppId = _activePlayer,
        SourceName = DisplayPlayer(_activePlayer),
        SourceKind = "PlayerCap",
        LyricSource = _activePlayer,
        IsShuffleActive = false,
        SupportsShuffle = false,
        Lyrics = _lyrics.ToArray()
    };

    private double GetPositionLocked(DateTime? now = null)
    {
        double position = _positionSec;
        if (_playing)
            position += ((now ?? DateTime.UtcNow) - _anchorUtc).TotalSeconds;

        if (_duration > TimeSpan.Zero)
            position = Math.Min(position, _duration.TotalSeconds);

        return Math.Max(0, position);
    }

    private void SetConnection(PlayerCapConnection connection, string detail)
    {
        lock (_gate)
        {
            _connection = connection;
            _statusDetail = detail;
        }
        ConnectionChanged?.Invoke(connection, detail);
    }

    private static async Task<bool> DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(milliseconds, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static T? Read<T>(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        try
        {
            return element.Deserialize<T>(JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private static PlaybackState MapState(string? status) => status?.ToLowerInvariant() switch
    {
        "playing" => PlaybackState.Playing,
        "paused" => PlaybackState.Paused,
        "standby" => PlaybackState.Stopped,
        "waiting_process" => PlaybackState.Stopped,
        "offline" => PlaybackState.Closed,
        "error" => PlaybackState.Closed,
        _ => PlaybackState.Stopped
    };

    public static string DisplayPlayer(string? player)
    {
        if (string.IsNullOrWhiteSpace(player)) return "PlayerCap";
        return player.ToLowerInvariant() switch
        {
            "qqmusic" or "qq" => "QQ音乐",
            "spotify" => "Spotify",
            "cloudmusic" or "netease" => "网易云音乐",
            "applemusic" or "apple-music" => "Apple Music",
            "kugou" => "酷狗音乐",
            "qishui" => "汽水音乐",
            "bodian" => "波点音乐",
            "wesing" => "全民K歌",
            _ => player
        };
    }

    private static string NormalizeHost(string? host)
    {
        var value = string.IsNullOrWhiteSpace(host) ? DefaultHost : host.Trim();
        value = value.Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
                     .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                     .Replace("ws://", string.Empty, StringComparison.OrdinalIgnoreCase)
                     .TrimEnd('/');
        return value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _http.Dispose();
        _coverLock.Dispose();
    }

    private sealed class ServiceEnvelope
    {
        [JsonPropertyName("data")]
        public ServiceStatus? Data { get; set; }
    }

    private sealed class ServiceStatus
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("player_support")]
        public List<string>? PlayerSupport { get; set; }

        [JsonPropertyName("player_running")]
        public List<string>? PlayerRunning { get; set; }

        [JsonPropertyName("player_status")]
        public Dictionary<string, string>? PlayerStatus { get; set; }
    }

    private sealed class StatusData
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("detail")]
        public string? Detail { get; set; }
    }

    private sealed class SongInfoData
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("singer")]
        public string? Singer { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("cover")]
        public string? Cover { get; set; }

        [JsonPropertyName("cover_base64")]
        public string? CoverBase64 { get; set; }
    }

    private sealed class AllLyricsData
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("position")]
        public double Position { get; set; }

        [JsonPropertyName("progress")]
        public double Progress { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("lyrics")]
        public List<CapLyricLine>? Lyrics { get; set; }
    }

    private sealed class CapLyricLine
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("timestamp")]
        public double Timestamp { get; set; }

        [JsonPropertyName("play_time")]
        public double PlayTime { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("sub_text")]
        public string? SubText { get; set; }
    }

    private sealed class LyricUpdateData
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("sub_text")]
        public string? SubText { get; set; }

        [JsonPropertyName("timestamp")]
        public double Timestamp { get; set; }

        [JsonPropertyName("play_time")]
        public double PlayTime { get; set; }

        [JsonPropertyName("position")]
        public double Position { get; set; }

        [JsonPropertyName("progress")]
        public double Progress { get; set; }
    }

    private sealed class PlaybackData
    {
        [JsonPropertyName("position")]
        public double Position { get; set; }

        [JsonPropertyName("progress")]
        public double Progress { get; set; }
    }

    private sealed class PlayerSwitchData
    {
        [JsonPropertyName("from")]
        public string? From { get; set; }

        [JsonPropertyName("to")]
        public string? To { get; set; }
    }
}
