namespace VideoFrameCut.Core;

public static class TimeSplitter
{
    /// <summary>
    /// Split at target times relative to the retained segment start, snapping each target
    /// to a displayed frame boundary. Existing manual/deleted boundaries are never crossed.
    /// </summary>
    public static IReadOnlyList<FrameRange> Split(DisplayFrameIndex index, FrameRange sourceRange, double seconds)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (sourceRange.End > index.FrameCount) throw new ArgumentOutOfRangeException(nameof(sourceRange));
        if (!double.IsFinite(seconds) || seconds < 0.05)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Split interval must be at least 0.05 seconds.");

        var start = index.BoundaryTime(sourceRange.Start);
        var end = index.BoundaryTime(sourceRange.End);
        var ranges = new List<FrameRange>();
        var last = sourceRange.Start;
        var target = start + seconds;
        var iterations = 0;
        // A tiny interval relative to the source FPS can yield many target times
        // snapping to one boundary; keep the loop bounded and explicit.
        while (target < end)
        {
            if (++iterations > 5_000_000)
                throw new InvalidOperationException("Too many automatic split boundaries.");
            var cut = index.NearestBoundary(target, sourceRange.Start, sourceRange.End);
            if (cut > last && cut < sourceRange.End)
            {
                ranges.Add(new FrameRange(last, cut));
                last = cut;
            }
            target += seconds;
        }

        if (last < sourceRange.End) ranges.Add(new FrameRange(last, sourceRange.End));
        return ranges;
    }
}
