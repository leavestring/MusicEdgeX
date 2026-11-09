namespace MusicEdge.Models;

/// <summary>
/// Represents the current track information from Windows SMTC.
/// </summary>
public class TrackInfo
{
    /// <summary>Song title (e.g., "晴天")</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Artist name (e.g., "周杰伦")</summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>Album title</summary>
    public string Album { get; set; } = string.Empty;

    /// <summary>Album year</summary>
    public string Year { get; set; } = string.Empty;

    /// <summary>Duration as TimeSpan</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;

    /// <summary>Current playback position</summary>
    public TimeSpan Position { get; set; } = TimeSpan.Zero;

    /// <summary>Playback state</summary>
    public PlaybackState State { get; set; } = PlaybackState.Stopped;

    /// <summary>Thumbnail image data (album art)</summary>
    public byte[]? ThumbnailBytes { get; set; }

    /// <summary>AppUserModelId of the app that owns the media session</summary>
    public string SourceAppId { get; set; } = string.Empty;

    /// <summary>Friendly name of the source app (QQ音乐, Spotify, ...)</summary>
    public string SourceName { get; set; } = "系统媒体";

    /// <summary>Whether the source session exposes shuffle control</summary>
    public bool IsShuffleActive { get; set; }

    public bool SupportsShuffle { get; set; }

    /// <summary>SMTC or PlayerCap</summary>
    public string SourceKind { get; set; } = "SMTC";

    /// <summary>Optional lyric source label (PlayerCap player id)</summary>
    public string LyricSource { get; set; } = string.Empty;

    /// <summary>Timed lyric lines when the source can provide them</summary>
    public IReadOnlyList<LyricLine> Lyrics { get; set; } = Array.Empty<LyricLine>();

    public bool IsQQMusic =>
        SourceAppId.Contains("QQMusic", StringComparison.OrdinalIgnoreCase) ||
        SourceName.Contains("QQ音乐", StringComparison.Ordinal);

    public override string ToString() =>
        string.IsNullOrEmpty(Title) ? "No track" : $"{Title} - {Artist}";

    public bool IsEmpty => string.IsNullOrEmpty(Title);
}

public enum PlaybackState
{
    Playing,
    Paused,
    Stopped,
    Closed
}
