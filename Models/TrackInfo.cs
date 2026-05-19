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

    /// <summary>Whether the track has been "liked"/favorited</summary>
    public bool IsLiked { get; set; }

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
