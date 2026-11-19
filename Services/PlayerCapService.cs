using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using MusicEdge.Models;

namespace MusicEdge.Services;

public enum PlayerCapConnection { Idle, Probing, Connecting, Connected, Disconnected, Unreachable }

public sealed class PlayerCapService : IDisposable
{
    private const int MaxArtworkBytes = 8 * 1024 * 1024;
    private const int MaxFrameBytes = 16 * 1024 * 1024;
    private const int InitialRetryMs = 3200;
    private const int MaxRetryMs = 30000;
    private const double MaxTrackSeconds = 7 * 24 * 60 * 60;

    public event Action? Changed;

    private readonly object _gate = new();
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(2.5),
        MaxResponseContentBufferSize = MaxArtworkBytes
    };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _host;
    private readonly string _requestedPlayer;
    private Task? _loop;
    private TrackInfo _track = new();
    private TimeSpan _anchorPosition;
    private long _anchorTimestamp;
    private long _trackVersion;
    private long _coverVersion;
    private CancellationTokenSource? _coverRequest;
    private string? _coverRequestKey;
    private bool _hasFullLyrics;
    private readonly List<(int? Index, LyricLine Line)> _streamedLyrics = [];
    private PlayerCapConnection _connection;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public PlayerCapService(string host, string player)
    {
        _host = NormalizeHost(host);
        _requestedPlayer = player.Trim();
    }

    public (PlayerCapConnection Connection, TrackInfo Track) GetSnapshot()
    {
        lock (_gate)
            return (_connection, _track with { Position = GetPositionLocked() });
    }

    public TimeSpan GetPosition()
    {
        lock (_gate)
            return GetPositionLocked();
    }

    public void Start()
    {
        var token = _lifetime.Token;
        _loop = Task.Run(() => RunAsync(token), token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        int retryMs = InitialRetryMs;
        try
        {
            while (!token.IsCancellationRequested)
            {
                SetConnection(PlayerCapConnection.Probing);
                var service = await ProbeAsync(token);
                if (token.IsCancellationRequested) return;

                if (service == null)
                {
                    SetConnection(PlayerCapConnection.Unreachable);
                }
                else
                {
                    using var socket = new ClientWebSocket();
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    SetConnection(PlayerCapConnection.Connecting);
                    try
                    {
                        var path = string.IsNullOrEmpty(_requestedPlayer) ? "/ws" :
                            $"/{Uri.EscapeDataString(_requestedPlayer)}/ws";
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(4));
                        await socket.ConnectAsync(new Uri($"ws://{_host}{path}"), timeout.Token);
                        ResetForPlayer(ChooseInitialPlayer(service));
                        SetConnection(PlayerCapConnection.Connected);
                        retryMs = InitialRetryMs;
                        await ReceiveLoopAsync(socket, token);
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        Debug.WriteLine(ex);
                    }

                    if (token.IsCancellationRequested) return;
                    SetConnection(PlayerCapConnection.Disconnected);
                }

                await Task.Delay(retryMs, token);
                retryMs = Math.Min(MaxRetryMs, retryMs * 2);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
    }

    private string ChooseInitialPlayer(ServiceStatus service)
    {
        if (_requestedPlayer.Length != 0) return _requestedPlayer;
        var supported = service.PlayerRunning?.Where(player => MusicPlayers.FromBridge(player) != null);
        return supported?.FirstOrDefault(player => service.PlayerStatus?.GetValueOrDefault(player) == "playing")
            ?? supported?.FirstOrDefault() ?? string.Empty;
    }

    private async Task<ServiceStatus?> ProbeAsync(CancellationToken token)
    {
        try
        {
            var json = await _http.GetStringAsync($"http://{_host}/service-status", token);
            return JsonSerializer.Deserialize<ServiceEnvelope>(json, JsonOptions)?.Data;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var frame = new MemoryStream();
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var packet = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (packet.MessageType == WebSocketMessageType.Close) return;
            if (packet.MessageType != WebSocketMessageType.Text) continue;
            if (frame.Length + packet.Count > MaxFrameBytes) return;

            frame.Write(buffer, 0, packet.Count);
            if (!packet.EndOfMessage) continue;
            HandleMessage(frame.GetBuffer().AsMemory(0, (int)frame.Length), token);
            frame.SetLength(0);
        }
    }

    private void HandleMessage(ReadOnlyMemory<byte> json, CancellationToken token)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return;

            string? type = typeElement.GetString();
            string? player = root.TryGetProperty("player", out var playerElement)
                ? playerElement.GetString() : null;
            var data = root.TryGetProperty("data", out var dataElement) ? dataElement : default;

            bool changed;
            lock (_gate)
            {
                if (token.IsCancellationRequested) return;

                if (type == "player_switch")
                {
                    var target = Read<PlayerSwitchData>(data)?.To ?? string.Empty;
                    if (_requestedPlayer.Length != 0 && !SamePlayer(_requestedPlayer, target)) return;
                    ResetForPlayer(target);
                    changed = true;
                }
                else
                {
                    bool playerChanged = false;
                    if (!string.IsNullOrWhiteSpace(player) && !SamePlayer(player, _track.SourceAppId))
                    {
                        if (_requestedPlayer.Length != 0 && !SamePlayer(player, _requestedPlayer)) return;
                        if (_track.SourceAppId.Length != 0 && type is not ("song_info_update" or "status_update"))
                            return;
                        if (_track.State == PlaybackState.Playing && !_track.IsEmpty &&
                            type == "status_update")
                            return;
                        ResetForPlayer(player);
                        playerChanged = true;
                    }

                    changed = type switch
                    {
                        "status_update" => HandleStatus(Read<StatusData>(data)),
                        "song_info_update" => HandleSongInfo(Read<SongInfoData>(data), token),
                        "all_lyrics" => HandleAllLyrics(Read<AllLyricsData>(data)),
                        "lyric_update" => HandleLyricUpdate(Read<LyricUpdateData>(data)),
                        "playback_pause" or "playback_resume" =>
                            HandlePlayback(Read<PlaybackData>(data), type == "playback_resume"),
                        "player_clear" => ClearTrack(),
                        "lyric_idle" => ClearLyrics(),
                        _ => false
                    };
                    changed |= playerChanged;
                }
            }

            if (changed) Changed?.Invoke();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            Debug.WriteLine(ex);
        }
    }

    private bool HandleStatus(StatusData? data)
    {
        if (data?.Status == null) return false;
        var state = data.Status.ToLowerInvariant() switch
        {
            "playing" => PlaybackState.Playing,
            "paused" => PlaybackState.Paused,
            "offline" or "error" => PlaybackState.Closed,
            _ => PlaybackState.Stopped
        };
        if (state is PlaybackState.Closed or PlaybackState.Stopped)
            return ClearTrack();
        return UpdateClock(data.Position, state);
    }

    private bool HandleSongInfo(SongInfoData? data, CancellationToken token)
    {
        if (data == null) return false;
        string title = string.IsNullOrWhiteSpace(data.Name) ? data.Title?.Trim() ?? "" : data.Name.Trim();
        string artist = data.Singer?.Trim() ?? "";
        if (title.Length == 0) return false;

        bool changed = _track.Title != title || _track.Artist != artist;
        if (changed)
        {
            var state = _track.State;
            ClearTrack();
            _track = _track with { Title = title, Artist = artist, State = state };
            _anchorTimestamp = Stopwatch.GetTimestamp();
        }

        string? coverKey = string.IsNullOrWhiteSpace(data.CoverBase64) ? data.Cover : data.CoverBase64;
        if (!string.IsNullOrWhiteSpace(coverKey) && _coverRequestKey != coverKey)
        {
            _coverRequestKey = coverKey;
            _coverRequest?.Cancel();
            var request = CancellationTokenSource.CreateLinkedTokenSource(token);
            _coverRequest = request;
            long trackVersion = _trackVersion;
            long coverVersion = ++_coverVersion;
            _ = Task.Run(() => LoadCoverAsync(data, request, trackVersion, coverVersion));
        }

        return changed;
    }

    private bool HandleAllLyrics(AllLyricsData? data)
    {
        if (_track.IsEmpty || data == null ||
            (!string.IsNullOrWhiteSpace(data.Title) && !MusicPlayers.SameText(data.Title, _track.Title)))
            return false;

        var ordered = (data.Lyrics ?? [])
            .Where(line => line.Index is not < 0 && !string.IsNullOrWhiteSpace(line.Text) &&
                           ValidSeconds(line.PlayTime > 0 ? line.PlayTime : line.Timestamp))
            .OrderBy(line => line.PlayTime > 0 ? line.PlayTime : line.Timestamp)
            .ToArray();
        var duration = ValidSeconds(data.Duration) ? TimeSpan.FromSeconds(data.Duration) : _track.Duration;
        var lyrics = new LyricLine[ordered.Length];
        for (int index = 0; index < ordered.Length; index++)
        {
            var line = ordered[index];
            var start = TimeSpan.FromSeconds(line.PlayTime > 0 ? line.PlayTime : line.Timestamp);
            var end = index + 1 < ordered.Length
                ? TimeSpan.FromSeconds(ordered[index + 1].PlayTime > 0
                    ? ordered[index + 1].PlayTime : ordered[index + 1].Timestamp)
                : duration;
            lyrics[index] = new LyricLine
            {
                Start = start,
                End = end > start ? end : start + TimeSpan.FromSeconds(6),
                Text = line.Text!.Trim(),
                Translation = line.SubText?.Trim() ?? ""
            };
        }

        bool positionChanged = UpdateClock(data.Position, _track.State);
        _hasFullLyrics = lyrics.Length != 0;
        _streamedLyrics.Clear();
        if (duration == _track.Duration && SameLyrics(lyrics, _track.Lyrics)) return positionChanged;
        _track = _track with { Duration = duration, Lyrics = lyrics.Length == 0 ? Array.Empty<LyricLine>() : lyrics };
        return true;
    }

    private bool HandleLyricUpdate(LyricUpdateData? data)
    {
        if (_track.IsEmpty || data == null) return false;
        bool positionChanged = UpdateClock(data.Position, _track.State);
        if (_hasFullLyrics) return positionChanged;

        string text = data.Text?.Trim() ?? "";
        string translation = data.SubText?.Trim() ?? "";
        if (text.Length == 0) return ClearLyrics() || positionChanged;

        bool hasStart = ValidSeconds(data.PlayTime) || ValidSeconds(data.Timestamp);
        var start = hasStart
            ? TimeSpan.FromSeconds(ValidSeconds(data.PlayTime) ? data.PlayTime : data.Timestamp)
            : GetPositionLocked();

        int existing = data.Index is >= 0
            ? _streamedLyrics.FindIndex(item => item.Index == data.Index)
            : hasStart ? _streamedLyrics.FindIndex(item => item.Line.Start == start) : -1;
        if (existing >= 0)
        {
            var previous = _streamedLyrics[existing].Line;
            if (previous.Text == text && previous.Translation == translation) return positionChanged;
            start = previous.Start;
            _streamedLyrics.RemoveAt(existing);
        }
        else if (!hasStart && _streamedLyrics.Count > 0 &&
                 _streamedLyrics[^1].Line.Text == text && _streamedLyrics[^1].Line.Translation == translation)
        {
            return positionChanged;
        }

        var line = new LyricLine
        {
            Start = start,
            End = start + TimeSpan.FromSeconds(6),
            Text = text,
            Translation = translation
        };
        int insertion = _streamedLyrics.FindIndex(item => item.Line.Start > start);
        _streamedLyrics.Insert(insertion < 0 ? _streamedLyrics.Count : insertion, (data.Index, line));
        if (_streamedLyrics.Count > 128) _streamedLyrics.RemoveAt(0);
        _track = _track with { Lyrics = _streamedLyrics.Select(item => item.Line).ToArray() };
        return true;
    }

    private bool HandlePlayback(PlaybackData? data, bool playing) =>
        UpdateClock(data?.Position ?? double.NaN, playing ? PlaybackState.Playing : PlaybackState.Paused);

    private bool UpdateClock(double seconds, PlaybackState state)
    {
        bool changed = state != _track.State;
        if (!changed && !ValidSeconds(seconds)) return false;

        var previous = GetPositionLocked();
        _anchorPosition = ValidSeconds(seconds) ? TimeSpan.FromSeconds(seconds) : previous;
        _anchorTimestamp = Stopwatch.GetTimestamp();
        if (changed) _track = _track with { State = state };
        return changed || (state != PlaybackState.Playing && _anchorPosition != previous);
    }

    private bool ClearLyrics()
    {
        _hasFullLyrics = false;
        _streamedLyrics.Clear();
        if (_track.Lyrics.Count == 0) return false;
        _track = _track with { Lyrics = Array.Empty<LyricLine>() };
        return true;
    }

    private void ResetForPlayer(string player)
    {
        lock (_gate)
        {
            ClearTrack();
            var id = MusicPlayers.FromBridge(player);
            _track = new TrackInfo
            {
                SourceAppId = player,
                SourceName = MusicPlayers.DisplayName(id),
                LyricSource = id ?? string.Empty
            };
        }
    }

    private bool ClearTrack()
    {
        bool changed = !_track.IsEmpty || _track.State != PlaybackState.Closed || _track.Lyrics.Count != 0;
        ++_trackVersion;
        _coverRequest?.Cancel();
        _coverRequest = null;
        _coverRequestKey = null;
        _hasFullLyrics = false;
        _streamedLyrics.Clear();
        _anchorPosition = TimeSpan.Zero;
        _anchorTimestamp = 0;
        _track = new TrackInfo
        {
            SourceAppId = _track.SourceAppId,
            SourceName = _track.SourceName,
            LyricSource = _track.LyricSource
        };
        return changed;
    }

    private async Task LoadCoverAsync(SongInfoData data, CancellationTokenSource request,
        long trackVersion, long coverVersion)
    {
        byte[]? bytes = null;
        var token = request.Token;
        try
        {
            if (token.IsCancellationRequested) return;
            var payload = data.CoverBase64;
            if (!string.IsNullOrWhiteSpace(payload))
            {
                if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = payload.IndexOf(',');
                    if (comma >= 0) payload = payload[(comma + 1)..];
                }

                if (payload.Length <= MaxArtworkBytes * 4 / 3 + 4)
                {
                    try { bytes = Convert.FromBase64String(payload); }
                    catch (FormatException) { }
                }
            }

            if (bytes == null && Uri.TryCreate(data.Cover, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https")
                bytes = await _http.GetByteArrayAsync(uri, token);

            if (bytes is not { Length: > 0 and <= MaxArtworkBytes }) return;
            lock (_gate)
            {
                if (token.IsCancellationRequested || trackVersion != _trackVersion || coverVersion != _coverVersion)
                    return;
                _track = _track with { ThumbnailBytes = bytes };
            }
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_coverRequest, request))
                {
                    _coverRequest = null;
                    if (bytes == null) _coverRequestKey = null;
                }
                request.Dispose();
            }
        }
    }

    private void SetConnection(PlayerCapConnection connection)
    {
        lock (_gate)
        {
            if (_connection == connection) return;
            _connection = connection;
            if (connection != PlayerCapConnection.Connected && !_track.IsEmpty)
                ClearTrack();
        }
        Changed?.Invoke();
    }

    private TimeSpan GetPositionLocked()
    {
        var position = _anchorPosition;
        if (_track.State == PlaybackState.Playing && _anchorTimestamp != 0)
            position += Stopwatch.GetElapsedTime(_anchorTimestamp);
        return _track.Duration > TimeSpan.Zero && position > _track.Duration ? _track.Duration : position;
    }

    private static bool SamePlayer(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
        (MusicPlayers.FromBridge(first) is { } id && id == MusicPlayers.FromBridge(second));

    private static bool SameLyrics(IReadOnlyList<LyricLine> first, IReadOnlyList<LyricLine> second)
    {
        if (first.Count != second.Count) return false;
        for (int index = 0; index < first.Count; index++)
        {
            var a = first[index];
            var b = second[index];
            if (a.Start != b.Start || a.End != b.End || a.Text != b.Text || a.Translation != b.Translation)
                return false;
        }
        return true;
    }

    private static bool ValidSeconds(double seconds) => seconds >= 0 && seconds <= MaxTrackSeconds;

    private static T? Read<T>(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object ? element.Deserialize<T>(JsonOptions) : default;

    private static string NormalizeHost(string host)
    {
        var value = string.IsNullOrWhiteSpace(host) ? "127.0.0.1:8765" : host.Trim();
        return value.Replace("http://", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("ws://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _http.Dispose();
        if (_loop == null) _lifetime.Dispose();
        else _ = _loop.ContinueWith(task =>
        {
            if (task.IsFaulted) Debug.WriteLine(task.Exception);
            _lifetime.Dispose();
        }, TaskScheduler.Default);
    }

    private sealed class ServiceEnvelope
    {
        [JsonPropertyName("data")] public ServiceStatus? Data { get; init; }
    }

    private sealed class ServiceStatus
    {
        [JsonPropertyName("player_running")] public List<string>? PlayerRunning { get; init; }
        [JsonPropertyName("player_status")] public Dictionary<string, string>? PlayerStatus { get; init; }
    }

    private sealed class StatusData : PlaybackData
    {
        [JsonPropertyName("status")] public string? Status { get; init; }
    }

    private sealed class SongInfoData
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("singer")] public string? Singer { get; init; }
        [JsonPropertyName("cover")] public string? Cover { get; init; }
        [JsonPropertyName("cover_base64")] public string? CoverBase64 { get; init; }
    }

    private sealed class AllLyricsData : PlaybackData
    {
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("duration")] public double Duration { get; init; } = double.NaN;
        [JsonPropertyName("lyrics")] public List<LyricUpdateData>? Lyrics { get; init; }
    }

    private sealed class LyricUpdateData : PlaybackData
    {
        [JsonPropertyName("index")] public int? Index { get; init; }
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("sub_text")] public string? SubText { get; init; }
        [JsonPropertyName("timestamp")] public double Timestamp { get; init; } = double.NaN;
        [JsonPropertyName("play_time")] public double PlayTime { get; init; } = double.NaN;
    }

    private class PlaybackData
    {
        [JsonPropertyName("position")] public double Position { get; init; } = double.NaN;
    }

    private sealed class PlayerSwitchData
    {
        [JsonPropertyName("to")] public string? To { get; init; }
    }
}
