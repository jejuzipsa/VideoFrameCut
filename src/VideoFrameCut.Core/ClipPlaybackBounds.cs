namespace VideoFrameCut.Core;

/// <summary>
/// Playback-only boundaries for an edited clip. End frame is exclusive; the last
/// visible frame is End - 1. Times are normalized to source playback time zero,
/// not absolute presentation timestamps.
/// </summary>
public sealed class ClipPlaybackBounds
{
    public FrameRange Range { get; }
    public int FirstFrame => Range.Start;
    public int LastFrame => Range.End - 1;
    public double StartSeconds { get; }
    public double EndSeconds { get; }
    public double DurationSeconds => EndSeconds - StartSeconds;

    public ClipPlaybackBounds(DisplayFrameIndex index, FrameRange range)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (range.End > index.FrameCount)
            throw new ArgumentOutOfRangeException(nameof(range));
        Range = range;
        StartSeconds = index.BoundaryTime(range.Start) - index.StartTime;
        EndSeconds = index.BoundaryTime(range.End) - index.StartTime;
    }

    public int ClampFrame(int frame) => Math.Clamp(frame, FirstFrame, LastFrame);
    public bool IsAtOrBeyondEnd(double playbackSeconds) =>
        double.IsFinite(playbackSeconds) && playbackSeconds >= EndSeconds;
}
