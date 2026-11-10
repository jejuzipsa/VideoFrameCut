namespace VideoFrameCut.Core;

public sealed record EditSegment(Guid Id, FrameRange SourceRange, bool ExportEnabled = true);
