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
    string? PixelFormat = null,
    string? CodecTag = null,
    // FFprobe stream display matrix / legacy rotate tag. Clockwise/counterclockwise
    // sign is irrelevant for width/height but is retained for diagnostics.
    int DisplayRotationDegrees = 0);
