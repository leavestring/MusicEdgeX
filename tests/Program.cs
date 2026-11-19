using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MusicEdge.Models;
using MusicEdge.Services;

static void Check(bool condition, string scenario)
{
    if (!condition) throw new InvalidOperationException(scenario);
}

Check(MusicPlayers.FromAppId(@"C:\Program Files\Tencent\QQMusic\QQMusic.exe") == "qqmusic", "Desktop QQ Music");
Check(MusicPlayers.FromAppId("NetEase.CloudMusic") == "cloudmusic", "NetEase app ID");
Check(MusicPlayers.FromAppId("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") == "spotify", "Store Spotify");
Check(MusicPlayers.FromAppId("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App") == "applemusic", "Store Apple Music");
foreach (var id in new[] { "Chrome", "Microsoft.MicrosoftEdge.Stable", "Microsoft.ZuneVideo",
             @"C:\Spotify\chrome.exe", "QQMusic_Helper.exe", "Video.QQMusic" })
    Check(MusicPlayers.FromAppId(id) == null, $"Excluded media app {id}");
Check(MusicPlayers.SessionPriority("cloudmusic", PlaybackState.Playing, false) >
      MusicPlayers.SessionPriority("qqmusic", PlaybackState.Paused, true), "Playing music wins");
Check(MusicPlayers.SessionPriority("cloudmusic", PlaybackState.Playing, true) >
      MusicPlayers.SessionPriority("qqmusic", PlaybackState.Playing, false), "Current music wins ties");

var lines = new[] { new LyricLine { Text = "第一句", Start = TimeSpan.FromSeconds(1) },
    new LyricLine { Text = "第二句", Start = TimeSpan.FromSeconds(5) } };
Check(LyricTiming.FindLine(lines, TimeSpan.FromSeconds(4.9), 100) == 1, "Positive offset advances");
Check(LyricTiming.FindLine(lines, TimeSpan.FromSeconds(5.1), -200) == 0, "Negative offset delays");
Check(LyricTiming.FindLine(lines, TimeSpan.FromSeconds(0.5), 0) == -1, "Before first line");
Check(JsonSerializer.Deserialize<AppSettings>("""{"EdgeSide":"Left"}""")!.Clone().LyricOffsetMs == 0,
    "Settings without an offset keep zero");
Check(new AppSettings { LyricOffsetMs = -400 }.Clone().LyricOffsetMs == -400, "Offset survives settings clone");

var system = new TrackInfo
{
    Title = "晴天", Artist = "周杰伦", SourceAppId = "QQMusic.exe", State = PlaybackState.Playing,
    Duration = TimeSpan.FromSeconds(269), HasMediaSession = true,
    Controls = PlaybackControls.PlayPause | PlaybackControls.Seek
};
var bridgeTrack = new TrackInfo
{
    Title = " 晴天 ", Artist = "周杰伦", SourceAppId = "qq", LyricSource = "qqmusic",
    State = PlaybackState.Playing, Lyrics = lines
};
var paused = system with { State = PlaybackState.Paused };
var merged = MusicSourceManager.SelectTrack(system, bridgeTrack, true, false);
Check(merged.Lyrics == lines && merged.SourceAppId == system.SourceAppId &&
      merged.Controls == system.Controls, "Matching bridge enriches the controlled session");
var bridgeClock = MusicSourceManager.SelectTrack(system with { Duration = TimeSpan.Zero },
    bridgeTrack with { Position = TimeSpan.FromSeconds(101), Duration = system.Duration }, true, false);
Check(bridgeClock.Position == TimeSpan.FromSeconds(101) && !bridgeClock.CanControl(PlaybackControls.Seek),
    "Bridge clock fills a missing system timeline and seek stays unavailable");
foreach (var mismatch in new[] { bridgeTrack with { Title = "旧曲" }, bridgeTrack with { Artist = "其他" },
             bridgeTrack with { SourceAppId = "cloudmusic" } })
    Check(MusicSourceManager.SelectTrack(system, mismatch, true, false).Lyrics.Count == 0, "Ignore mismatched lyrics");
Check(MusicSourceManager.SelectTrack(system with { SourceAppId = "Chrome" }, null, false, true).IsEmpty,
    "Browser-only playback stays idle");
Check(MusicSourceManager.SelectTrack(paused, null, true, false).State == PlaybackState.Paused,
    "Paused music remains when nothing else is playing");
Check(MusicSourceManager.SelectTrack(new TrackInfo(), bridgeTrack with { State = PlaybackState.Paused },
    true, true).IsEmpty, "Other media suppresses a paused music session");
Check(MusicSourceManager.SelectTrack(new TrackInfo(), bridgeTrack, false, false).Controls == PlaybackControls.None,
    "Bridge-only song has no system controls");

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var token = timeout.Token;
var reservation = new TcpListener(IPAddress.Loopback, 0);
reservation.Start();
int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
reservation.Stop();
using var server = new HttpListener();
server.Prefixes.Add($"http://127.0.0.1:{port}/");
server.Start();
using var bridge = new PlayerCapService($"127.0.0.1:{port}", "");
bridge.Start();
var probe = await server.GetContextAsync().WaitAsync(token);
Check(probe.Request.Url?.AbsolutePath == "/service-status", "Service probe path");
var status = Encoding.UTF8.GetBytes("""{"data":{"player_running":["qqmusic"]}}""");
probe.Response.ContentLength64 = status.Length;
await probe.Response.OutputStream.WriteAsync(status, token);
probe.Response.Close();
var upgrade = await server.GetContextAsync().WaitAsync(token);
Check(upgrade.Request.Url?.AbsolutePath == "/ws", "WebSocket path");
using var socket = (await upgrade.AcceptWebSocketAsync(null).WaitAsync(token)).WebSocket;

async Task Send(string json)
{
    var bytes = Encoding.UTF8.GetBytes(json);
    int split = Array.FindIndex(bytes, value => value >= 128) + 1;
    if (split > 0)
        await socket.SendAsync(bytes.AsMemory(0, split), WebSocketMessageType.Text, false, token);
    await socket.SendAsync(bytes.AsMemory(split), WebSocketMessageType.Text, true, token);
}

async Task<TrackInfo> WaitFor(Func<PlayerCapConnection, TrackInfo, bool> predicate)
{
    while (true)
    {
        var snapshot = bridge.GetSnapshot();
        if (predicate(snapshot.Connection, snapshot.Track)) return snapshot.Track;
        await Task.Delay(15, token);
    }
}

await Send("""{"type":"song_info_update","player":"qqmusic","data":{"name":"晴天","singer":"周杰伦"}}""");
await Send("""{"type":"playback_resume","data":{"position":2}}""");
await Send("""{"type":"lyric_update","data":{"index":0,"play_time":1,"text":"第一句","position":2}}""");
await Send("""{"type":"lyric_update","data":{"index":1,"play_time":5,"text":"第二句","sub_text":"Second line","position":5}}""");
var streamed = await WaitFor((_, track) => track.Lyrics.Count == 2);
Check(streamed.Title == "晴天" && streamed.Lyrics[1].Text == "第二句", "Fragmented UTF-8 and consecutive streaming lyrics");
Check(LyricTiming.FindLine(streamed.Lyrics, TimeSpan.FromSeconds(5), -500) == 0, "Streaming history supports a delay");

await Send("""{"type":"song_info_update","player":"qqmusic","data":{"name":"夜曲","singer":"周杰伦"}}""");
await Send("""{"type":"all_lyrics","data":{"title":"晴天","lyrics":[{"index":0,"timestamp":1,"text":"旧歌词"}]}}""");
await Send("""{"type":"playback_pause","data":{"position":7}}""");
var switched = await WaitFor((_, track) => track.Title == "夜曲" && track.State == PlaybackState.Paused);
Check(switched.Lyrics.Count == 0 && switched.Position == TimeSpan.FromSeconds(7), "Track change rejects late lyrics");
await Send("""{"type":"all_lyrics","data":{"title":"夜曲","duration":125,"position":7,"lyrics":[{"index":1,"timestamp":5,"text":"然后"},{"index":0,"timestamp":1,"text":"开始"}]}}""");
var full = await WaitFor((_, track) => track.Lyrics.Count == 2);
Check(full.Duration == TimeSpan.FromSeconds(125) && full.Lyrics[0].Text == "开始", "Full lyrics are sorted");
await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture done", token);
var disconnected = await WaitFor((connection, _) => connection == PlayerCapConnection.Disconnected);
Check(disconnected.IsEmpty && disconnected.Lyrics.Count == 0, "Disconnect clears bridge snapshot");

Console.WriteLine("Music source, lyric timing, settings and PlayerCap integration checks passed.");
