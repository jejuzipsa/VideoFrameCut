namespace VideoFrameCut.Core;

/// <summary>Seek by real presentation timestamps; retain the active clip frame bounds.</summary>
public static class FrameNavigation
{
    public static int BySeconds(
        DisplayFrameIndex index,
        int currentFrame,
        double seconds,
        FrameRange? selectedClip = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!double.IsFinite(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds));

        var first = selectedClip?.Start ?? 0;
        var last = (selectedClip?.End ?? index.FrameCount) - 1;
        if (first < 0 || first > last || last >= index.FrameCount)
            throw new ArgumentOutOfRangeException(nameof(selectedClip));

        var current = Math.Clamp(currentFrame, first, last);
        var targetPts = index.BoundaryTime(current) + seconds;
        return index.NearestBoundary(targetPts, first, last);
    }
}
