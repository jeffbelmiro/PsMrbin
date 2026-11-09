namespace TempBin.Services;

public static class AdaptiveSlugPolicy
{
    public const int MinimumLength = 12;
    public const int MaximumLength = 16;

    public static int LengthFor(long activePublishers, long recentCreations)
    {
        var length = MinimumLength;
        // One extra character per tenfold increase after 100 recent publishing identities.
        for (long threshold = 100; activePublishers >= threshold && length < MaximumLength; threshold *= 10) length++;
        // Keep estimated namespace occupancy below one in a trillion, within the length cap.
        while (length < MaximumLength && recentCreations > Math.Pow(SlugGenerator.Alphabet.Length, length) / 1e12) length++;
        return length;
    }
}
