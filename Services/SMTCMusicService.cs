using System.IO;
using System.Runtime.InteropServices;
using MusicEdge.Models;

namespace MusicEdge.Services;

/// <summary>
/// Reads current track information from Windows SystemMediaTransportControls (SMTC).
/// QQ Music automatically registers with SMTC on Windows 10+, so this works without any reverse engineering.
/// </summary>
public class SMTCMusicService
{
    // This uses the Windows.Media.Control API via interop
    // GSMTCCreateManager - creates the GlobalSystemMediaTransportControlsSessionManager
    public event Action<TrackInfo>? TrackChanged;
    public event Action<PlaybackState>? PlaybackStateChanged;

    private TrackInfo _currentTrack = new();
    private System.Timers.Timer? _pollTimer;
    private readonly object _lock = new();
    private bool _isRunning;

    public TrackInfo CurrentTrack
    {
        get { lock (_lock) return _currentTrack; }
        private set
        {
            TrackInfo old;
            lock (_lock)
            {
                old = _currentTrack;
                _currentTrack = value;
            }
            if (old.Title != value.Title || old.Artist != value.Artist ||
                old.Album != value.Album || old.State != value.State)
            {
                TrackChanged?.Invoke(value);
            }
            if (old.State != value.State)
            {
                PlaybackStateChanged?.Invoke(value.State);
            }
        }
    }

    /// <summary>
    /// Start polling SMTC for track changes. Poll interval 500ms.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _pollTimer = new System.Timers.Timer(500);
        _pollTimer.Elapsed += (s, e) => _ = PollSMTCAsync();
        _pollTimer.AutoReset = true;
        _pollTimer.Start();
        _ = PollSMTCAsync(); // Immediate first poll
    }

    /// <summary>
    /// Stop polling.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private async Task PollSMTCAsync()
    {
        try
        {
            var track = await GetCurrentTrackFromSMTCAsync();
            if (track != null)
            {
                CurrentTrack = track;
            }
        }
        catch
        {
            // SMTC may not be available (no media app running)
            var old = CurrentTrack;
            if (old.State != PlaybackState.Closed)
            {
                CurrentTrack = new TrackInfo { State = PlaybackState.Closed };
            }
        }
    }

    private async Task<TrackInfo?> GetCurrentTrackFromSMTCAsync()
    {
        // Using Windows.Media.Control GlobalSystemMediaTransportControlsSessionManager
        // This requires the Microsoft.Windows.CsWinRT package for WinRT interop in .NET

        var manager = await GetSessionManagerAsync();
        if (manager == null) return null;

        // Get the current session (QQ Music should be the active one)
        var session = manager.GetCurrentSession();
        if (session == null)
        {
            // Fallback: enumerate all sessions and pick the one with media properties
            var sessions = manager.GetSessions();
            foreach (var s in sessions)
            {
                try
                {
                    var info = s.GetPlaybackInfo();
                    if (info?.PlaybackStatus != null &&
                        info.PlaybackStatus != Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
                    {
                        session = s;
                        break;
                    }
                }
                catch { }
            }
        }

        if (session == null) return null;

        var track = new TrackInfo();
        var playbackInfo = session.GetPlaybackInfo();

        track.State = playbackInfo?.PlaybackStatus switch
        {
            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackState.Stopped,
            _ => PlaybackState.Stopped
        };

        // Get media properties
        var mediaProps = await session.TryGetMediaPropertiesAsync();
        if (mediaProps != null)
        {
            track.Title = mediaProps.Title ?? string.Empty;
            track.Artist = mediaProps.Artist ?? string.Empty;
            track.Album = mediaProps.AlbumTitle ?? string.Empty;
            track.Year = mediaProps.AlbumTrackCount > 0 ? mediaProps.TrackNumber.ToString() : string.Empty;

            // Get thumbnail
            if (mediaProps.Thumbnail != null)
            {
                try
                {
                    using var stream = await mediaProps.Thumbnail.OpenReadAsync();
                    using var memStream = new MemoryStream();
                    await stream.AsStream().CopyToAsync(memStream);
                    track.ThumbnailBytes = memStream.ToArray();
                }
                catch
                {
                    track.ThumbnailBytes = null;
                }
            }
        }

        // Get timeline properties (position / duration)
        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline != null)
            {
                track.Position = timeline.Position;
                track.Duration = timeline.EndTime - timeline.StartTime;
            }
        }
        catch { }

        return track;
    }

    // Cache the session manager to avoid repeated creation
    private static object? _cachedManager;
    private static readonly object _managerLock = new();
    private static DateTime _managerLastCreated = DateTime.MinValue;

    private static async Task<Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager?> GetSessionManagerAsync()
    {
        lock (_managerLock)
        {
            if (_cachedManager != null && (DateTime.UtcNow - _managerLastCreated).TotalMinutes < 2)
                return (Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager)_cachedManager;
        }

        try
        {
            var manager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_managerLock)
            {
                _cachedManager = manager;
                _managerLastCreated = DateTime.UtcNow;
            }
            return manager;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Send a playback command via SMTC (as a supplement to key simulation).
    /// </summary>
    public async Task<bool> TryPlayAsync()
    {
        var session = await GetCurrentSessionAsync();
        if (session == null) return false;
        await session.TryPlayAsync();
        return true;
    }

    public async Task<bool> TryPauseAsync()
    {
        var session = await GetCurrentSessionAsync();
        if (session == null) return false;
        await session.TryPauseAsync();
        return true;
    }

    public async Task<bool> TrySkipNextAsync()
    {
        var session = await GetCurrentSessionAsync();
        if (session == null) return false;
        await session.TrySkipNextAsync();
        return true;
    }

    public async Task<bool> TrySkipPreviousAsync()
    {
        var session = await GetCurrentSessionAsync();
        if (session == null) return false;
        await session.TrySkipPreviousAsync();
        return true;
    }

    private async Task<Windows.Media.Control.GlobalSystemMediaTransportControlsSession?> GetCurrentSessionAsync()
    {
        var manager = await GetSessionManagerAsync();
        return manager?.GetCurrentSession();
    }
}
