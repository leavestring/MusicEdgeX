namespace MusicEdge.Models;

internal static class LyricTiming
{
    public static int FindLine(IReadOnlyList<LyricLine> lyrics, TimeSpan position, int offsetMs)
    {
        double milliseconds = position.TotalMilliseconds + offsetMs;
        int low = 0;
        int high = lyrics.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (lyrics[middle].Start.TotalMilliseconds <= milliseconds)
                low = middle + 1;
            else
                high = middle;
        }
        return low - 1;
    }
}
