namespace HeyDanis.Capture;

/// <summary>
/// Claude's image resizing rule (platform.claude.com/docs/en/build-with-claude/vision-coordinates).
/// Claude returns pointing coordinates in the space of the image it actually looks at; if it
/// downsizes our screenshot internally, every coordinate is off. So we resize screenshots
/// ourselves to exactly the size Claude would use — then no server-side resize happens and the
/// returned coordinates map 1:1 onto our screenshot.
/// </summary>
public static class ClaudeImageSizing
{
    /// <summary>Claude 4.7 and later (incl. Haiku 5.5 / Sonnet 5.5): long edge 2576 px, 4784 visual tokens.</summary>
    public static readonly ImageTier HighResolution = new(2576, 4784);

    /// <summary>Older models: long edge 1568 px, 1568 visual tokens.</summary>
    public static readonly ImageTier Standard = new(1568, 1568);

    /// <summary>
    /// Tier used for new screenshots. Starts at high resolution; switched to standard for the
    /// rest of the session if the API reports that it would have to downsize our images.
    /// </summary>
    public static ImageTier CurrentTier { get; set; } = HighResolution;

    /// <summary>Visual tokens: one per 28x28 pixel patch.</summary>
    public static int CountImageTokens(int width, int height) =>
        (width + 27) / 28 * ((height + 27) / 28);

    /// <summary>
    /// The size Claude resizes an image to before padding (reference implementation from the
    /// docs). Images that already fit are returned unchanged.
    /// </summary>
    public static (int Width, int Height) ResizedSize(int width, int height, ImageTier tier)
    {
        bool Fits(int w, int h) =>
            (w + 27) / 28 * 28 <= tier.MaxEdge
            && (h + 27) / 28 * 28 <= tier.MaxEdge
            && CountImageTokens(w, h) <= tier.MaxVisualTokens;

        if (Fits(width, height))
            return (width, height);

        if (height > width)
        {
            var (resizedHeight, resizedWidth) = ResizedSize(height, width, tier);
            return (resizedWidth, resizedHeight);
        }

        // Binary search along the long edge for the largest aspect-preserving size that fits.
        // The short edge rounds half to even, matching the live API at exact .5 ties.
        var aspectRatio = (double)width / height;
        int ShortEdge(int longEdge) => Math.Max((int)Math.Round(longEdge / aspectRatio, MidpointRounding.ToEven), 1);

        var lo = 1;
        var hi = width;
        while (lo + 1 < hi)
        {
            var mid = (lo + hi) / 2;
            if (Fits(mid, ShortEdge(mid)))
                lo = mid;
            else
                hi = mid;
        }
        return (lo, ShortEdge(lo));
    }
}

/// <summary>Image limits of one Claude resolution tier.</summary>
public sealed record ImageTier(int MaxEdge, int MaxVisualTokens);
