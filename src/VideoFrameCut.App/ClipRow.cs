using System.ComponentModel;
using System.Runtime.CompilerServices;
using VideoFrameCut.Core;

namespace VideoFrameCut.App;

public sealed class ClipRow : INotifyPropertyChanged
{
    private bool _exportEnabled;

    public Guid Id { get; }
    public int Number { get; }
    public string FromTime { get; }
    public string ToTime { get; }
    public string RangeText { get; }
    public int FrameCount { get; }
    public string Duration { get; }

    public bool ExportEnabled
    {
        get => _exportEnabled;
        set
        {
            if (_exportEnabled == value) return;
            _exportEnabled = value;
            OnPropertyChanged();
            ExportChanged?.Invoke(this, value);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<ClipRow, bool>? ExportChanged;

    public ClipRow(int sequence, EditSegment segment, DisplayFrameIndex index)
    {
        Id = segment.Id;
        Number = sequence;
        _exportEnabled = segment.ExportEnabled;
        var range = segment.SourceRange;
        FromTime = Format(index.BoundaryTime(range.Start));
        ToTime = Format(index.BoundaryTime(range.End));
        RangeText = range.ToString();
        FrameCount = range.Count;
        Duration = Format(index.BoundaryTime(range.End) - index.BoundaryTime(range.Start));
    }

    private static string Format(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\.fff");

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
