using System.IO;
using VideoFrameCut.Core;
using VideoFrameCut.Media;

namespace VideoFrameCut.App;

/// <summary>One imported source and its independent, in-memory edit history.</summary>
public sealed class SourceVideoRow
{
    public string Path { get; }
    public string Name { get; }
    public string Details { get; }
    public SourceVideoInfo Media { get; }
    public DisplayFrameIndex Index { get; }
    public ClipEditor Editor { get; }

    public SourceVideoRow(SourceVideoInfo media, DisplayFrameIndex index)
    {
        Media = media;
        Index = index;
        Editor = new ClipEditor(index);
        Path = System.IO.Path.GetFullPath(media.Path);
        Name = System.IO.Path.GetFileName(Path);
        Details = $"{index.FrameCount:N0}프레임 · " +
            $"{TimeSpan.FromSeconds(index.EndTime - index.StartTime):hh\\:mm\\:ss} · " +
            $"{media.Width}×{media.Height}";
    }
}
