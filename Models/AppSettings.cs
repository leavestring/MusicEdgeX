namespace MusicEdge.Models;

public sealed class AppSettings
{
    public string EdgeSide { get; set; } = "Right";

    public int HoverDelayMs { get; set; } = 300;

    public int CollapseDelayMs { get; set; } = 650;

    public int HideDelayMs { get; set; } = 1400;

    public bool DisableInFullscreen { get; set; } = true;

    public bool AutoStart { get; set; } = false;

    public bool StartPinned { get; set; } = false;

    public bool UsePlayerCap { get; set; } = true;

    public string PlayerCapHost { get; set; } = "127.0.0.1:8765";

    public string PlayerCapPlayer { get; set; } = string.Empty;

    public AppSettings Clone() => new()
    {
        EdgeSide = EdgeSide,
        HoverDelayMs = HoverDelayMs,
        CollapseDelayMs = CollapseDelayMs,
        HideDelayMs = HideDelayMs,
        DisableInFullscreen = DisableInFullscreen,
        AutoStart = AutoStart,
        StartPinned = StartPinned,
        UsePlayerCap = UsePlayerCap,
        PlayerCapHost = PlayerCapHost,
        PlayerCapPlayer = PlayerCapPlayer
    };
}
