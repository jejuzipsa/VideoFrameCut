namespace VideoFrameCut.Core;

/// <summary>Pure, non-destructive edit list. All ranges refer to the original source.</summary>
public sealed class ClipEditor
{
    private readonly Stack<EditSegment[]> _undo = new();
    private readonly Stack<EditSegment[]> _redo = new();
    private List<EditSegment> _segments;

    public DisplayFrameIndex Index { get; }
    public IReadOnlyList<EditSegment> Segments => _segments.ToArray();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public ClipEditor(DisplayFrameIndex index)
    {
        Index = index ?? throw new ArgumentNullException(nameof(index));
        _segments = new List<EditSegment> { new(Guid.NewGuid(), new FrameRange(0, index.FrameCount)) };
    }

    public bool SplitAt(int frameBoundary)
    {
        var position = _segments.FindIndex(s => s.SourceRange.ContainsBoundary(frameBoundary));
        if (position < 0) return false;
        Remember();
        var selected = _segments[position];
        _segments[position] = selected with
        {
            SourceRange = new FrameRange(selected.SourceRange.Start, frameBoundary)
        };
        _segments.Insert(position + 1, selected with
        {
            Id = Guid.NewGuid(),
            SourceRange = new FrameRange(frameBoundary, selected.SourceRange.End)
        });
        return true;
    }

    /// <summary>
    /// Remove only the cut boundaries between selected source-adjacent clips.
    /// Missing, non-consecutive or separated-by-deletion selections are rejected
    /// without touching edit history.
    /// </summary>
    public bool CanMerge(IEnumerable<Guid> segmentIds)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        var ids = segmentIds.ToHashSet();
        if (ids.Count < 2) return false;

        var selectedPositions = Enumerable.Range(0, _segments.Count)
            .Where(i => ids.Contains(_segments[i].Id)).ToArray();
        if (selectedPositions.Length != ids.Count || selectedPositions.Length < 2)
            return false;

        var first = selectedPositions[0];
        var last = selectedPositions[^1];
        if (last - first + 1 != ids.Count) return false;

        for (var i = first; i < last; i++)
        {
            if (_segments[i].SourceRange.End != _segments[i + 1].SourceRange.Start)
                return false;
        }
        return true;
    }

    public bool Merge(IEnumerable<Guid> segmentIds)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        var ids = segmentIds.ToHashSet();
        if (!CanMerge(ids)) return false;

        var first = _segments.FindIndex(s => ids.Contains(s.Id));
        var count = ids.Count;
        var last = first + count - 1;
        var sourceStart = _segments[first].SourceRange.Start;
        var sourceEnd = _segments[last].SourceRange.End;

        // A merged clip is only auto-exported if ALL its selected parts were
        // export-enabled. Previously excluded material must not be re-enabled.
        var enabled = _segments.GetRange(first, count).All(s => s.ExportEnabled);
        var merged = _segments[first] with
        {
            SourceRange = new FrameRange(sourceStart, sourceEnd),
            ExportEnabled = enabled
        };

        Remember();
        _segments.RemoveRange(first, count);
        _segments.Insert(first, merged);
        EnsureInvariants();
        return true;
    }

    public bool Delete(IEnumerable<Guid> segmentIds)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        var ids = segmentIds.ToHashSet();
        if (!_segments.Any(segment => ids.Contains(segment.Id))) return false;
        Remember();
        _segments.RemoveAll(segment => ids.Contains(segment.Id));
        return true;
    }

    public bool SetExportEnabled(IEnumerable<Guid> segmentIds, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        var ids = segmentIds.ToHashSet();
        if (!_segments.Any(s => ids.Contains(s.Id) && s.ExportEnabled != enabled)) return false;
        Remember();
        _segments = _segments.Select(s => ids.Contains(s.Id) ? s with { ExportEnabled = enabled } : s).ToList();
        return true;
    }

    public bool SplitByTime(double seconds)
    {
        var replacements = _segments
            .Select(s => (Original: s, Ranges: TimeSplitter.Split(Index, s.SourceRange, seconds)))
            .ToList();
        if (replacements.All(item => item.Ranges.Count == 1)) return false;

        Remember();
        _segments = replacements.SelectMany(item => item.Ranges.Select((range, n) =>
            item.Original with { Id = n == 0 ? item.Original.Id : Guid.NewGuid(), SourceRange = range })).ToList();
        EnsureInvariants();
        return true;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(_segments.ToArray());
        _segments = _undo.Pop().ToList();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(_segments.ToArray());
        _segments = _redo.Pop().ToList();
        return true;
    }

    public IReadOnlyList<EditSegment> ExportSelection() => _segments.Where(s => s.ExportEnabled).ToArray();

    private void Remember()
    {
        _undo.Push(_segments.ToArray());
        _redo.Clear();
    }

    private void EnsureInvariants()
    {
        var previousEnd = 0;
        foreach (var segment in _segments)
        {
            if (segment.SourceRange.Start < previousEnd || segment.SourceRange.End > Index.FrameCount)
                throw new InvalidOperationException("Overlapping or invalid original source frame ranges.");
            previousEnd = segment.SourceRange.End;
        }
    }
}
