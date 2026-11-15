using System.Globalization;
using VideoFrameCut.Media;

namespace VideoFrameCut.Export;

/// <summary>Three deliberately simple presets. All settings apply to a NEW output file.</summary>
public enum CompressionPreset
{
    HighQuality = 0,
    Balanced = 1,
    SmallFile = 2
}

/// <summary>Positive values rotate clockwise relative to the displayed source video.</summary>
public sealed record VideoTransformOptions(
    CompressionPreset Preset = CompressionPreset.HighQuality,
    int RotationDegrees = 0,
    bool FlipHorizontal = false);

public sealed record VideoTranscodePlan(
    int Width, int Height, int? FramesPerSecond, bool IsHdr, bool TenBit,
    bool CopyAudio, int NvencCq, int CpuCrf,
    CompressionPreset Preset, int RotationDegrees, bool FlipHorizontal);

public sealed record VideoTranscodeProgress(double? Percent, string Message);

public sealed record VideoTranscodeResult(
    string OutputPath, int Width, int Height, long FileSizeBytes,
    string Encoder, bool HdrPreserved);

/// <summary>
/// Re-encodes a whole source to a new HEVC MP4 using FFmpeg. Preserves HDR
/// transfer/color tags and 10-bit precision (rather than silently converting
/// HLG/PQ into incorrect SDR). NVENC is tried first; libx265 is the fallback.
/// Rotation and horizontal mirroring are baked into the OUTPUT video pixels.
/// </summary>
public sealed class VideoTranscodeExporter
{
    public string FfmpegPath { get; }

    public VideoTranscodeExporter(string ffmpegPath = "ffmpeg") =>
        FfmpegPath = ffmpegPath;

    public static VideoTranscodePlan CreatePlan(
        SourceVideoInfo source, VideoTransformOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Preset))
            throw new ArgumentOutOfRangeException(nameof(options), "지원하지 않는 압축 방식입니다.");
        if (options.RotationDegrees is not (0 or 90 or 180 or 270))
            throw new ArgumentOutOfRangeException(nameof(options),
                "회전은 0°, 90°, 180°, 270°만 지원합니다.");
        if (source.Width <= 0 || source.Height <= 0)
            throw new ArgumentException("원본 영상의 해상도가 올바르지 않습니다.");
        var (maxW, maxH, cq, crf) = options.Preset switch
        {
            CompressionPreset.HighQuality => (source.Width, source.Height, 19, 20),
            CompressionPreset.Balanced when source.Width >= source.Height =>
                (1920, 1080, 24, 24),
            CompressionPreset.Balanced => (1080, 1920, 24, 24),
            CompressionPreset.SmallFile when source.Width >= source.Height =>
                (1280, 720, 28, 28),
            CompressionPreset.SmallFile => (720, 1280, 28, 28),
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
        // A max bounding box NEVER upscales an already smaller source.
        var factor = Math.Min(1.0, Math.Min(
            maxW / (double)source.Width, maxH / (double)source.Height));
        var width = Math.Max(2, 2 * (int)Math.Floor(source.Width * factor / 2));
        var height = Math.Max(2, 2 * (int)Math.Floor(source.Height * factor / 2));
        if (options.RotationDegrees is 90 or 270)
            (width, height) = (height, width);

        var hdr = source.ColorTransfer is "arib-std-b67" or "smpte2084";
        var tenBit = hdr ||
            (options.Preset == CompressionPreset.HighQuality &&
             source.PixelFormat?.Contains("10", StringComparison.OrdinalIgnoreCase) == true);
        var audioCopy = options.Preset != CompressionPreset.SmallFile &&
            string.Equals(source.AudioCodec, "aac", StringComparison.OrdinalIgnoreCase);
        var fps = options.Preset == CompressionPreset.SmallFile &&
            source.NominalFramesPerSecond is > 30
            ? 30 : (int?)null;
        return new VideoTranscodePlan(width, height, fps, hdr, tenBit,
            audioCopy, cq, crf, options.Preset, options.RotationDegrees,
            options.FlipHorizontal);
    }

    public static string FilterFor(SourceVideoInfo source, VideoTranscodePlan plan)
    {
        // Scale before rotation, so 4K landscape -> 1080p landscape -> 1080x1920
        // for a 90-degree turn. A final hflip mirrors screen-left/right AFTER rotation.
        var targetW = plan.RotationDegrees is 90 or 270 ? plan.Height : plan.Width;
        var targetH = plan.RotationDegrees is 90 or 270 ? plan.Width : plan.Height;
        var filters = new List<string>();
        if (targetW != source.Width || targetH != source.Height)
            filters.Add($"scale={targetW}:{targetH}:flags=lanczos");
        if (plan.FramesPerSecond.HasValue)
            filters.Add($"fps={plan.FramesPerSecond.Value}");
        switch (plan.RotationDegrees)
        {
            case 90: filters.Add("transpose=clock"); break;
            case 180: filters.Add("hflip"); filters.Add("vflip"); break;
            case 270: filters.Add("transpose=cclock"); break;
        }
        if (plan.FlipHorizontal) filters.Add("hflip");
        filters.Add("setsar=1");
        filters.Add(plan.TenBit ? "format=yuv420p10le" : "format=yuv420p");
        return string.Join(',', filters);
    }

    public async Task<VideoTranscodeResult> ExportAsync(
        SourceVideoInfo source, string outputPath, VideoTransformOptions options,
        IProgress<VideoTranscodeProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var plan = CreatePlan(source, options);
        if (!File.Exists(source.Path))
            throw new FileNotFoundException("원본 영상 파일이 없습니다.", source.Path);
        var input = Path.GetFullPath(source.Path);
        var output = Path.GetFullPath(outputPath);
        if (Path.GetExtension(output).ToLowerInvariant() != ".mp4")
            throw new ArgumentException("MP4 출력 파일명만 지원합니다.", nameof(outputPath));
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("원본 영상 파일에 덮어쓸 수 없습니다.", nameof(outputPath));
        if (File.Exists(output))
            throw new IOException("같은 이름의 파일이 이미 있습니다. 다른 이름으로 저장하세요.");

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var pending = Path.Combine(Path.GetDirectoryName(output)!,
            "." + Path.GetFileNameWithoutExtension(output) + "." +
            Guid.NewGuid().ToString("N") + ".partial.mp4");
        var encoder = "hevc_nvenc";
        try
        {
            progress?.Report(new VideoTranscodeProgress(0, "HEVC NVENC 인코딩 준비 중..."));
            try
            {
                await RunEncoderAsync(source, plan, pending, encoder, progress, cancellation);
                await VerifyOutputAsync(source, plan, pending, cancellation);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                ProcessingDiagnostics.Record("FALLBACK", "압축 HEVC NVENC 실패 → CPU 재시도",
                    ex, $"원본: {source.Path}\n출력: {output}\n프리셋: {options.Preset}");
                // Some NVENC builds omit HLG/PQ bitstream tags. A visually
                // plausible but incorrectly-tagged HDR output is not acceptable:
                // retry with x265's explicitly configured HDR VUI instead.
                if (File.Exists(pending)) File.Delete(pending);
                encoder = "libx265";
                progress?.Report(new VideoTranscodeProgress(0,
                    "GPU 인코딩/검증 실패 · CPU HEVC로 안전하게 다시 시도합니다."));
                await RunEncoderAsync(source, plan, pending, encoder, progress, cancellation);
                await VerifyOutputAsync(source, plan, pending, cancellation);
            }

            cancellation.ThrowIfCancellationRequested();
            File.Move(pending, output, overwrite: false);
            var length = new FileInfo(output).Length;
            progress?.Report(new VideoTranscodeProgress(100, "영상 압축·회전·반전 완료"));
            return new VideoTranscodeResult(output, plan.Width, plan.Height, length,
                encoder, plan.IsHdr);
        }
        finally
        {
            try { if (File.Exists(pending)) File.Delete(pending); } catch (IOException) { }
        }
    }

    private static async Task VerifyOutputAsync(SourceVideoInfo source,
        VideoTranscodePlan plan, string pending, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!File.Exists(pending) || new FileInfo(pending).Length < 512)
            throw new InvalidDataException("FFmpeg에서 출력된 MP4 파일을 확인하지 못했습니다.");

        var actual = await new MediaProbe().ProbeAsync(pending, cancellation);
        if (actual.Codec != "hevc" || actual.CodecTag != "hvc1" ||
            actual.Width != plan.Width || actual.Height != plan.Height)
            throw new InvalidDataException(
                $"출력 해상도/코덱 태그 불일치: {actual.Width}×{actual.Height}, {actual.Codec}/{actual.CodecTag}");

        if (plan.IsHdr &&
            (actual.ColorTransfer != source.ColorTransfer ||
             actual.ColorPrimaries != "bt2020" ||
             actual.PixelFormat?.Contains("10", StringComparison.Ordinal) != true))
            throw new InvalidDataException(
                "HDR 전송 특성/색역/10비트 보존 검증 실패. 이 파일은 저장하지 않습니다.");

        if (source.HasAudio && actual.AudioCodec != "aac")
            throw new InvalidDataException("출력 음성 AAC 검증 실패.");
        if (!source.HasAudio && actual.HasAudio)
            throw new InvalidDataException("원본에 없는 음성이 추가되었습니다.");
    }

    internal static IReadOnlyList<string> Arguments(SourceVideoInfo source,
        VideoTranscodePlan plan, string target, string encoder)
    {
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string> {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", source.Path,
            "-map", "0:v:0", "-map_metadata", "-1", "-map_chapters", "-1",
            "-sn", "-dn", "-vf", FilterFor(source, plan),
            "-c:v", encoder
        };
        if (encoder == "hevc_nvenc")
            args.AddRange(new[] { "-preset", "p5", "-rc:v", "vbr", "-cq:v",
                plan.NvencCq.ToString(inv), "-b:v", "0",
                "-pix_fmt", plan.TenBit ? "p010le" : "yuv420p" });
        else
            args.AddRange(new[] { "-preset", "medium", "-crf",
                plan.CpuCrf.ToString(inv), "-pix_fmt",
                plan.TenBit ? "yuv420p10le" : "yuv420p" });

        if (plan.TenBit) args.AddRange(new[] { "-profile:v", "main10" });
        if (plan.IsHdr && encoder == "libx265")
        {
            // x265 reads color VUI from its own encoder parameters. Merely
            // passing -color_trc to FFmpeg does not always set the HEVC bitstream
            // VUI; the resulting MP4 may otherwise report 'unknown' for HLG/PQ.
            var transfer = source.ColorTransfer == "smpte2084" ? "16" : "18";
            args.AddRange(new[] { "-x265-params",
                $"colorprim=9:transfer={transfer}:colormatrix=9" });
        }
        if (plan.IsHdr)
        {
            // Avoid 8-bit SDR conversion of HLG / PQ. 10-bit BT.2020 is kept
            // even in lower-resolution/small-file modes.
            args.AddRange(new[] { "-color_primaries", "bt2020", "-color_trc",
                source.ColorTransfer!, "-colorspace", "bt2020nc" });
        }

        if (source.HasAudio)
        {
            args.AddRange(new[] { "-map", "0:a:0" });
            if (plan.Preset == CompressionPreset.SmallFile)
                args.AddRange(new[] { "-c:a", "aac", "-b:a", "96k" });
            else if (plan.CopyAudio)
                args.AddRange(new[] { "-c:a", "copy" });
            else
                args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
        }
        else args.Add("-an");

        if (plan.FramesPerSecond.HasValue)
            args.AddRange(new[] { "-r", plan.FramesPerSecond.Value.ToString(inv),
                "-fps_mode:v", "cfr" });
        else
            args.AddRange(new[] { "-fps_mode:v", "passthrough" });
        // FFmpeg defaults to the HEVC MP4 sample entry 'hev1'. Windows
        // thumbnail handlers and Apple-oriented media consumers commonly
        // expect 'hvc1' instead. Unlike re-encoding this only changes the
        // sample-description identifier; preserve the original HDR/10-bit data.
        args.AddRange(new[] { "-tag:v", "hvc1", "-movflags", "+faststart",
            "-metadata:s:v:0", "rotate=0", "-progress", "pipe:1", "-nostats",
            "-y", target });
        return args;
    }

    private async Task RunEncoderAsync(SourceVideoInfo source, VideoTranscodePlan plan,
        string target, string encoder, IProgress<VideoTranscodeProgress>? progress,
        CancellationToken cancellation)
    {
        var seconds = source.ContainerDurationSeconds;
        await FfProcess.ForEachLineAsync(FfmpegPath,
            Arguments(source, plan, target, encoder),
            line =>
            {
                if (line.StartsWith("out_time=", StringComparison.Ordinal) &&
                    TimeSpan.TryParse(line["out_time=".Length..],
                        CultureInfo.InvariantCulture, out var time) && seconds is > 0)
                {
                    var percent = Math.Clamp(time.TotalSeconds / seconds.Value * 100, 0, 99);
                    progress?.Report(new VideoTranscodeProgress(percent,
                        $"압축 진행 {percent:0}% · {encoder}"));
                }
            }, cancellation);
    }
}
