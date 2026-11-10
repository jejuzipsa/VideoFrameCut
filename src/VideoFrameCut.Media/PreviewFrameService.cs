using VideoFrameCut.Core;

namespace VideoFrameCut.Media;

/// <summary>
/// Exact display-frame still extraction. Slow for late source frames because it
/// deliberately decodes from the source start. Playback may use a fast platform
/// preview, but this still is authoritative for frame-boundary inspection.
/// </summary>
public sealed class PreviewFrameService
{
    private readonly string _ffmpeg;
    public PreviewFrameService(string ffmpeg = "ffmpeg") => _ffmpeg = ffmpeg;

    public async Task<byte[]> GrabPngAsync(string sourcePath, int displayFrameIndex,
        CancellationToken cancellation = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Missing source video.", sourcePath);
        if (displayFrameIndex < 0) throw new ArgumentOutOfRangeException(nameof(displayFrameIndex));

        var output = Path.Combine(Path.GetTempPath(), $"framecut_{Guid.NewGuid():N}.png");
        try
        {
            await FfProcess.CaptureAsync(_ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-i", sourcePath,
                "-vf", $"select='eq(n,{displayFrameIndex})'",
                "-an", "-frames:v", "1", "-y", output
            }, cancellation);
            return await File.ReadAllBytesAsync(output, cancellation);
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }
        }
    }
}
