using System.Globalization;
using System.Text.Json;
using VideoFrameCut.Core;
using VideoFrameCut.Media;

namespace VideoFrameCut.Export;

/// <summary>
/// Reference export path: full decode, exact display-frame selection and H.264 encode.
/// This deliberately favors correctness over keyframe-copy performance.
/// </summary>
public sealed class ClipExporter
{
    public string FfmpegPath { get; }
    public MediaProbe Probe { get; }

    public ClipExporter(string ffmpegPath = "ffmpeg", string ffprobePath = "ffprobe")
    {
        FfmpegPath = ffmpegPath;
        Probe = new MediaProbe(ffprobePath);
    }

    public async Task<ExportReport> ExportAsync(
        SourceVideoInfo source,
        DisplayFrameIndex index,
        IReadOnlyList<EditSegment> clips,
        string outputFolder,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Quality < 0 || settings.Quality > 51)
            throw new ArgumentOutOfRangeException(nameof(settings), "Quality must be 0..51.");
        Directory.CreateDirectory(outputFolder);

        var selected = clips.Where(c => c.ExportEnabled).ToArray();
        Validate(index, selected);
        var started = DateTimeOffset.UtcNow;
        var results = new List<ClipExportResult>();
        for (var i = 0; i < selected.Length; i++)
        {
            if (cancellation.IsCancellationRequested) break;
            var clip = selected[i];
            var name = $"{System.IO.Path.GetFileNameWithoutExtension(source.Path)}_{i + 1:000}.mp4";
            var output = System.IO.Path.Combine(outputFolder, name);
            var pending = output + ".partial.mp4";
            var encoder = settings.Encoding == EncodingMode.Software ? "libx264" : "h264_nvenc";
            var expected = clip.SourceRange.Count;
            var measured = (int?)null;
            var succeeded = false;
            string? error = null;
            try
            {
                if (File.Exists(output)) throw new IOException($"Output file already exists: {output}");
                progress?.Report(new ExportProgress(i, selected.Length, name, "Encoding"));
                try
                {
                    await EncodeAsync(source, index, clip.SourceRange, pending, settings, encoder, cancellation);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) when (settings.Encoding == EncodingMode.Auto && encoder == "h264_nvenc")
                {
                    DeleteTemporary(pending);
                    encoder = "libx264";
                    progress?.Report(new ExportProgress(i, selected.Length, name, "NVENC failed; retrying with CPU"));
                    await EncodeAsync(source, index, clip.SourceRange, pending, settings, encoder, cancellation);
                }

                progress?.Report(new ExportProgress(i, selected.Length, name, "Decoding output for verification"));
                measured = await Probe.CountDecodedFramesAsync(pending, cancellation);
                if (measured != expected)
                    throw new InvalidDataException($"Expected {expected} decoded frames; got {measured}.");
                File.Move(pending, output, overwrite: false);
                succeeded = true;
            }
            catch (OperationCanceledException)
            {
                error = "Canceled";
                results.Add(new ClipExportResult(i + 1, clip.SourceRange.Start, clip.SourceRange.End,
                    name, expected, measured, false, encoder, error));
                DeleteTemporary(pending);
                break;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                DeleteTemporary(pending);
            }
            results.Add(new ClipExportResult(i + 1, clip.SourceRange.Start, clip.SourceRange.End,
                name, expected, measured, succeeded, encoder, error));
            progress?.Report(new ExportProgress(i + 1, selected.Length, name, succeeded ? "Verified" : "Failed"));
        }

        var report = new ExportReport(source.Path, "preserve-source-pts", started, DateTimeOffset.UtcNow, results);
        var manifestFile = System.IO.Path.Combine(outputFolder, "framecut_manifest.json");
        await File.WriteAllTextAsync(manifestFile, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));
        return report;
    }

    private async Task EncodeAsync(SourceVideoInfo source, DisplayFrameIndex index,
        FrameRange range, string path, ExportSettings settings, string encoder, CancellationToken cancellation)
    {
        var fmt = CultureInfo.InvariantCulture;
        var from = index.BoundaryTime(range.Start).ToString("R", fmt);
        var to = index.BoundaryTime(range.End).ToString("R", fmt);
        var withAudio = settings.KeepAudio && source.HasAudio;
        var cfr = IsConstantRate(index, source.NominalFramesPerSecond);
        var stripFps = !cfr && await SupportsStripFpsAsync(cancellation) ? ":strip_fps=1" : "";
        var filter = $"[0:v:0]select='gte(n,{range.Start})*lt(n,{range.End})',setpts=PTS-STARTPTS{stripFps}[v]";
        if (withAudio) filter += $";[0:a:0]atrim=start={from}:end={to},asetpts=PTS-STARTPTS[a]";

        var args = new List<string> {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", source.Path, "-filter_complex", filter,
            "-map", "[v]"
        };
        if (withAudio) args.AddRange(new[] { "-map", "[a]", "-c:a", "aac", "-b:a", "192k" });
        else args.Add("-an");

        // CFR requires an explicit output frame rate to give the final MP4 sample
        // a nonzero duration. With VFR, passthrough preserves original PTS gaps.
        if (cfr)
        {
            args.AddRange(new[] { "-r", source.NominalFramesPerSecond!.Value.ToString("0.########", fmt),
                "-fps_mode:v", "cfr" });
        }
        else
        {
            args.AddRange(new[] { "-fps_mode:v", "passthrough", "-use_editlist", "0" });
        }
        args.AddRange(new[] { "-c:v", encoder });
        // Without an MP4 edit list, B-frame decoder reordering can shift the
        // first VFR presentation timestamp away from zero. Disable B-frames on
        // VFR output to keep the first displayed frame at PTS 0.
        if (!cfr) args.AddRange(new[] { "-bf:v", "0" });
        if (encoder == "h264_nvenc")
            args.AddRange(new[] { "-preset", "p5", "-rc:v", "vbr", "-cq:v", settings.Quality.ToString(fmt), "-b:v", "0" });
        else
            args.AddRange(new[] { "-preset", "medium", "-crf", settings.Quality.ToString(fmt) });
        args.AddRange(new[] { "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-y", path });

        await FfProcess.CaptureAsync(FfmpegPath, args, cancellation);
    }

    private async Task<bool> SupportsStripFpsAsync(CancellationToken cancellation)
    {
        var version = await FfProcess.CaptureAsync(FfmpegPath, new[] { "-version" }, cancellation);
        var match = System.Text.RegularExpressions.Regex.Match(version,
            @"ffmpeg version (?:n)?(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var major) && major >= 8;
    }

    private static bool IsConstantRate(DisplayFrameIndex index, double? nominalFps)
    {
        if (nominalFps is not > 0) return false;
        if (index.FrameCount == 1) return true;
        var expected = 1.0 / nominalFps.Value;
        var tolerance = Math.Max(0.0012, expected * 0.025);
        for (var i = 1; i < index.FrameCount; i++)
        {
            if (Math.Abs(index.BoundaryTime(i) - index.BoundaryTime(i - 1) - expected) > tolerance)
                return false;
        }
        return true;
    }

    private static void Validate(DisplayFrameIndex index, IReadOnlyList<EditSegment> clips)
    {
        var previousEnd = 0;
        foreach (var clip in clips.OrderBy(c => c.SourceRange.Start))
        {
            var range = clip.SourceRange;
            if (range.Start < previousEnd || range.End > index.FrameCount)
                throw new InvalidOperationException("Overlapping or out-of-source output ranges.");
            previousEnd = range.End;
        }
    }

    private static void DeleteTemporary(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}
