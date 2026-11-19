using System.Text;
using MusicEdge.Models;

namespace MusicEdge.Services;

internal static class MusicPlayers
{
    // Compare complete identity components so a browser title or a package with
    // a look-alike name cannot qualify as a music player.
    public static string? FromAppId(string id)
    {
        var leaf = id[(id.LastIndexOfAny(['\\', '/']) + 1)..];
        if (leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            leaf = leaf[..^4];
        else
        {
            int separator = leaf.IndexOf('!');
            if (separator >= 0)
                leaf = leaf[..separator].Split('_', 2)[0];
        }

        return leaf.ToLowerInvariant() switch
        {
            "qqmusic" or "tencent.qqmusic" or "qq音乐" => "qqmusic",
            "cloudmusic" or "netease.cloudmusic" or "neteasecloudmusic" or "网易云音乐" => "cloudmusic",
            "spotify" or "spotifyab.spotifymusic" => "spotify",
            "applemusic" or "apple.music" or "appleinc.applemusic" or "appleinc.applemusicwin" => "applemusic",
            "kugou" or "kugoumusic" or "酷狗音乐" => "kugou",
            "qishui" or "qishuimusic" or "汽水音乐" => "qishui",
            _ => null
        };
    }

    public static string? FromBridge(string id) => id.Trim().ToLowerInvariant() switch
    {
        "qqmusic" or "qq" => "qqmusic",
        "cloudmusic" or "netease" => "cloudmusic",
        "spotify" => "spotify",
        "applemusic" or "apple-music" => "applemusic",
        "kugou" => "kugou",
        "qishui" => "qishui",
        _ => null
    };

    public static string DisplayName(string? id) => id switch
    {
        "qqmusic" => "QQ 音乐",
        "cloudmusic" => "网易云音乐",
        "spotify" => "Spotify",
        "applemusic" => "Apple Music",
        "kugou" => "酷狗音乐",
        "qishui" => "汽水音乐",
        _ => string.Empty
    };

    public static int SessionPriority(string? player, PlaybackState state, bool current) =>
        (player, state) switch
        {
            (not null, PlaybackState.Playing) => current ? 4 : 3,
            (not null, PlaybackState.Paused) => current ? 2 : 1,
            _ => 0
        };

    public static bool SameSong(TrackInfo system, TrackInfo bridge)
    {
        var player = FromBridge(bridge.SourceAppId);
        return player != null && player == FromAppId(system.SourceAppId) &&
               !system.IsEmpty && !bridge.IsEmpty && SameText(system.Title, bridge.Title) &&
               (string.IsNullOrWhiteSpace(system.Artist) || string.IsNullOrWhiteSpace(bridge.Artist) ||
                SameText(system.Artist, bridge.Artist));
    }

    public static bool SameText(string first, string second) =>
        string.Equals(first.Trim().Normalize(NormalizationForm.FormKC),
            second.Trim().Normalize(NormalizationForm.FormKC), StringComparison.OrdinalIgnoreCase);
}
