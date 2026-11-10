namespace VideoFrameCut.Core;

/// <summary>A source display-frame interval [Start, End); End is exclusive.</summary>
public readonly record struct FrameRange
{
    public int Start { get; }
    public int End { get; }
    public int Count => End - Start;

    public FrameRange(int start, int end)
    {
        if (start < 0 || end <= start)
            throw new ArgumentOutOfRangeException(nameof(start), "Expected 0 <= start < end.");
        Start = start;
        End = end;
    }

    public bool ContainsBoundary(int boundary) => Start < boundary && boundary < End;
    public bool Overlaps(FrameRange other) => Start < other.End && other.Start < End;
    public override string ToString() => $"[{Start}, {End})";
}
