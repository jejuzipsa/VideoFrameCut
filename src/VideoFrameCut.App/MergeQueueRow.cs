using VideoFrameCut.Export;

namespace VideoFrameCut.App;

/// <summary>One immutable snapshot of a selected original-frame range in the merge queue.</summary>
public sealed class MergeQueueRow
{
    public VideoMergeClip Clip { get; }
    public string SourceName => Path.GetFileName(Clip.Source.Path);
    public string Details { get; }
    public string Label => $"{SourceName}  ·  {Details}";

    public MergeQueueRow(VideoMergeClip clip)
    {
        Clip = clip;
        var from = Math.Max(0, clip.Index.BoundaryTime(clip.Range.Start) - clip.Index.StartTime);
        var to = Math.Max(0, clip.Index.BoundaryTime(clip.Range.End) - clip.Index.StartTime);
        Details = $"{TimeSpan.FromSeconds(from):hh\\:mm\\:ss\\.fff} → " +
            $"{TimeSpan.FromSeconds(to):hh\\:mm\\:ss\\.fff} · {clip.Range.Count:N0}프레임";
    }
}
