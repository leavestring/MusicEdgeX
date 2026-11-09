namespace MusicEdge.Models;

public sealed class LyricLine
{
    public TimeSpan Start { get; init; }

    public TimeSpan End { get; init; }

    public string Text { get; init; } = string.Empty;

    public string Translation { get; init; } = string.Empty;

    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);
}
