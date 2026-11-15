namespace VideoFrameCut.Media;

public sealed record SourceVideoInfo(
    string Path,
    int Width,
    int Height,
    string Codec,
    double? NominalFramesPerSecond,
    bool HasAudio,
    double? ContainerDurationSeconds,
    string? AudioCodec = null,
    string? ColorTransfer = null,
    string? ColorPrimaries = null,
    string? ColorSpace = null,
    string? PixelFormat = null);
