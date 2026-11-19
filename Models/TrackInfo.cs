namespace MusicEdge.Models;

/// <summary>
/// Represents the current track information from Windows SMTC.
/// </summary>
public sealed record TrackInfo
{
    /// <summary>Song title (e.g., "晴天")</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Artist name (e.g., "周杰伦")</summary>
    public string Artist { get; init; } = string.Empty;

    /// <summary>Album title</summary>
    public string Album { get; init; } = string.Empty;

    /// <summary>Duration as TimeSpan</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Current playback position</summary>
    public TimeSpan Position { get; init; }

    /// <summary>Playback state</summary>
    public PlaybackState State { get; init; } = PlaybackState.Closed;

    /// <summary>Thumbnail image data (album art)</summary>
    public byte[]? ThumbnailBytes { get; init; }

    /// <summary>AppUserModelId of the app that owns the media session</summary>
    public string SourceAppId { get; init; } = string.Empty;

    /// <summary>Friendly name of the source app (QQ音乐, Spotify, ...)</summary>
    public string SourceName { get; init; } = string.Empty;

    public bool IsShuffleActive { get; init; }
    public PlaybackControls Controls { get; init; }
    public bool HasMediaSession { get; init; }

    /// <summary>Optional lyric source label (PlayerCap player id)</summary>
    public string LyricSource { get; init; } = string.Empty;

    /// <summary>Timed lyric lines when the source can provide them</summary>
    public IReadOnlyList<LyricLine> Lyrics { get; init; } = Array.Empty<LyricLine>();

    public bool CanControl(PlaybackControls control) => (Controls & control) != 0;

    public bool IsQQMusic =>
        SourceAppId.Contains("QQMusic", StringComparison.OrdinalIgnoreCase) ||
        SourceName == "QQ 音乐";

    public override string ToString() =>
        string.IsNullOrEmpty(Title) ? "No track" : $"{Title} - {Artist}";

    public bool IsEmpty => string.IsNullOrWhiteSpace(Title);
}

[Flags]
public enum PlaybackControls
{
    None = 0,
    PlayPause = 1,
    Previous = 2,
    Next = 4,
    Seek = 8,
    Shuffle = 16
}

public enum PlaybackState
{
    Playing,
    Paused,
    Stopped,
    Closed
}
