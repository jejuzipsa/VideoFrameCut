namespace VideoFrameCut.Export;

public enum EncodingMode { Auto, Software }

public sealed record ExportSettings(
    EncodingMode Encoding = EncodingMode.Auto,
    bool KeepAudio = true,
    int Quality = 18);

public sealed record ExportProgress(int Completed, int Total, string CurrentOutput, string Status);

public sealed record ClipExportResult(
    int Sequence,
    int StartFrame,
    int EndFrameExclusive,
    string OutputFile,
    int ExpectedFrames,
    int? DecodedFrames,
    bool Succeeded,
    string Encoder,
    string? Error);

public sealed record ExportReport(
    string Source,
    string TimingPolicy,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<ClipExportResult> Clips);
