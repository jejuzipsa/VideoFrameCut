namespace VideoFrameCut.Core;

/// <summary>Decoded/display order, not packet/DTS order; timestamps are in source seconds.</summary>
public sealed class DisplayFrameIndex
{
    private readonly double[] _times;

    public int FrameCount => _times.Length;
    public double StartTime => _times[0];
    public double EndTime { get; }
    public IReadOnlyList<double> Timestamps => Array.AsReadOnly(_times);

    public DisplayFrameIndex(IEnumerable<double> displayPtsSeconds, double endTime)
    {
        ArgumentNullException.ThrowIfNull(displayPtsSeconds);
        _times = displayPtsSeconds.ToArray();
        if (_times.Length == 0) throw new ArgumentException("Video has no display frames.", nameof(displayPtsSeconds));
        for (var i = 0; i < _times.Length; i++)
        {
            if (!double.IsFinite(_times[i]) || (i > 0 && _times[i] <= _times[i - 1]))
                throw new ArgumentException($"Invalid or non-increasing display PTS at frame {i}.", nameof(displayPtsSeconds));
        }
        if (!double.IsFinite(endTime) || endTime <= _times[^1])
            throw new ArgumentOutOfRangeException(nameof(endTime), "End boundary must follow the last frame.");
        EndTime = endTime;
    }

    public double BoundaryTime(int boundary)
    {
        if (boundary < 0 || boundary > FrameCount) throw new ArgumentOutOfRangeException(nameof(boundary));
        return boundary == FrameCount ? EndTime : _times[boundary];
    }

    /// <summary>Nearest real frame boundary. In a tie, choose the earlier boundary.</summary>
    public int NearestBoundary(double seconds, int minimum, int maximum)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (minimum < 0 || maximum > FrameCount || minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum));
        if (seconds <= BoundaryTime(minimum)) return minimum;
        if (seconds >= BoundaryTime(maximum)) return maximum;

        var lo = minimum;
        var hi = maximum;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (BoundaryTime(mid) < seconds) lo = mid + 1;
            else hi = mid;
        }
        var before = lo - 1;
        var distanceBefore = seconds - BoundaryTime(before);
        var distanceAfter = BoundaryTime(lo) - seconds;
        return distanceBefore <= distanceAfter ? before : lo;
    }

    public static DisplayFrameIndex FromConstantFrameRate(int count, int numerator, int denominator = 1)
    {
        if (count <= 0 || numerator <= 0 || denominator <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        var interval = (double)denominator / numerator;
        return new DisplayFrameIndex(Enumerable.Range(0, count).Select(n => n * interval), count * interval);
    }
}
