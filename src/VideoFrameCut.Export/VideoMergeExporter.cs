using System.Globalization;
using System.Text;
using VideoFrameCut.Core;
using VideoFrameCut.Media;

namespace VideoFrameCut.Export;

public enum MergeTransitionMode
{
    Cut,
    Crossfade,
    FadeThroughBlack
}

/// <summary>Snapshot of a source and exactly the original display frames selected for merging.</summary>
public sealed record VideoMergeClip(SourceVideoInfo Source, DisplayFrameIndex Index, FrameRange Range);

public sealed record VideoMergeSettings(
    MergeTransitionMode Transition = MergeTransitionMode.Cut,
    double TransitionSeconds = 0.5,
    bool KeepAudio = true,
    EncodingMode Encoding = EncodingMode.Auto);

public sealed record VideoMergeProgress(int Completed, int Total, string Message);

public sealed record VideoMergeResult(
    string OutputFile, int Width, int Height, int FramesPerSecond,
    int OutputFrames, MergeTransitionMode Transition, int SourceCount);

public sealed record VideoMergePlan(
    int Width, int Height, int FramesPerSecond, int TransitionFrames,
    IReadOnlyList<int> ClipFrames, int ExpectedFrames)
{
    public double TransitionSeconds => TransitionFrames / (double)FramesPerSecond;
}

/// <summary>
/// Frame-selected FFmpeg concat and xfade pipeline across independent video sources.
/// All source selection uses decoded display frame numbers. When different sources are
/// merged, the resulting presentation must use a common CFR, size and pixel format;
/// this does NOT alter the sources or single-source export engine.
/// </summary>
public sealed class VideoMergeExporter
{
    public string FfmpegPath { get; }
    public MediaProbe Probe { get; }

    public VideoMergeExporter(string ffmpegPath = "ffmpeg", string ffprobePath = "ffprobe")
    {
        FfmpegPath = ffmpegPath;
        Probe = new MediaProbe(ffprobePath);
    }

    public static VideoMergePlan Plan(IReadOnlyList<VideoMergeClip> clips, VideoMergeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(settings);
        if (clips.Count < 2) throw new ArgumentException("병합 목록에 구간을 2개 이상 넣어 주세요.");
        if (clips.Count > 100) throw new ArgumentException("한 번에 병합할 수 있는 구간은 최대 100개입니다.");
        if (!Enum.IsDefined(settings.Transition))
            throw new ArgumentOutOfRangeException(nameof(settings), "지원되지 않는 전환 방식입니다.");
        if (!double.IsFinite(settings.TransitionSeconds) ||
            settings.TransitionSeconds < 0.1 || settings.TransitionSeconds > 3.0)
            throw new ArgumentOutOfRangeException(nameof(settings),
                "전환 시간은 0.1~3.0초 사이로 설정하세요.");

        var first = clips[0].Source;
        var width = Math.Max(2, first.Width / 2 * 2);
        var height = Math.Max(2, first.Height / 2 * 2);
        if (width > 7680 || height > 4320)
            throw new ArgumentException("첫 번째 영상의 해상도가 너무 큽니다.");
        // User selects a common timeline rate by the first source. Rounded
        // integer Hz avoids FFmpeg floating-point timebase drift with xfade.
        var fps = (int)Math.Clamp(Math.Round(first.NominalFramesPerSecond is > 0
            ? first.NominalFramesPerSecond.Value : 30), 1, 120);
        var frames = new int[clips.Count];
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            ArgumentNullException.ThrowIfNull(clip.Source);
            ArgumentNullException.ThrowIfNull(clip.Index);
            if (clip.Range.Start < 0 || clip.Range.End > clip.Index.FrameCount)
                throw new ArgumentException($"병합 구간 {i + 1}: 원본 프레임 범위가 잘못됐습니다.");
            if (!File.Exists(clip.Source.Path))
                throw new FileNotFoundException("병합 목록의 원본 파일을 찾을 수 없습니다.", clip.Source.Path);
            var duration = clip.Index.BoundaryTime(clip.Range.End) -
                clip.Index.BoundaryTime(clip.Range.Start);
            if (!double.IsFinite(duration) || duration <= 0)
                throw new ArgumentException($"병합 구간 {i + 1}: 영상 길이가 잘못됐습니다.");
            frames[i] = Math.Max(1, (int)Math.Round(duration * fps, MidpointRounding.AwayFromZero));
        }

        var transitionFrames = settings.Transition == MergeTransitionMode.Cut
            ? 0 : Math.Max(1, (int)Math.Round(settings.TransitionSeconds * fps));
        if (transitionFrames > 0)
        {
            for (var i = 0; i < frames.Length; i++)
                if (frames[i] <= transitionFrames)
                    throw new ArgumentException(
                        $"병합 구간 {i + 1}이 전환 시간({transitionFrames / (double)fps:0.###}초)보다 짧습니다. " +
                        "전환 시간을 줄이거나 더 긴 구간을 추가하세요.");
        }
        var total = frames.Sum() - transitionFrames * (frames.Length - 1);
        if (total < 1) throw new ArgumentException("최종 영상 길이가 유효하지 않습니다.");
        return new VideoMergePlan(width, height, fps, transitionFrames, frames, total);
    }

    public async Task<VideoMergeResult> ExportAsync(
        IReadOnlyList<VideoMergeClip> clips, string outputFile,
        VideoMergeSettings settings,
        IProgress<VideoMergeProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var plan = Plan(clips, settings);
        var path = Path.GetFullPath(outputFile);
        if (!string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("병합 출력은 MP4 파일만 지원합니다.", nameof(outputFile));
        if (File.Exists(path)) throw new IOException("출력 파일이 이미 존재합니다. 다른 이름을 선택하세요.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var directory = Path.Combine(Path.GetTempPath(), "FrameCut-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var segments = new List<string>();
        var outputPartial = Path.Combine(Path.GetDirectoryName(path)!,
            "." + Path.GetFileNameWithoutExtension(path) + "." + Guid.NewGuid().ToString("N") + ".partial.mp4");
        var encoder = settings.Encoding == EncodingMode.Software ? "libx264" : "h264_nvenc";
        try
        {
            for (var i = 0; i < clips.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var intermediate = Path.Combine(directory, $"segment_{i:000}.mp4");
                progress?.Report(new VideoMergeProgress(i, clips.Count + 2,
                    $"{i + 1}/{clips.Count} 원본 구간 프레임 추출 중..."));
                await NormalizeAsync(clips[i], intermediate, plan, plan.ClipFrames[i],
                    settings.KeepAudio, cancellation);
                segments.Add(intermediate);
            }

            progress?.Report(new VideoMergeProgress(clips.Count, clips.Count + 2,
                settings.Transition == MergeTransitionMode.Cut
                    ? "영상들을 컷 연결하는 중..." : "선택한 페이드 전환을 적용하는 중..."));
            try
            {
                await ComposeAsync(segments, plan, settings, outputPartial, encoder, cancellation);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (settings.Encoding == EncodingMode.Auto &&
                encoder == "h264_nvenc")
            {
                encoder = "libx264";
                if (File.Exists(outputPartial)) File.Delete(outputPartial);
                progress?.Report(new VideoMergeProgress(clips.Count, clips.Count + 2,
                    "NVENC를 사용할 수 없어 CPU 인코더로 다시 시도합니다."));
                await ComposeAsync(segments, plan, settings, outputPartial, encoder, cancellation);
            }

            progress?.Report(new VideoMergeProgress(clips.Count + 1, clips.Count + 2,
                "최종 영상의 디코딩 프레임을 검증하는 중..."));
            var decodedFrames = await Probe.CountDecodedFramesAsync(outputPartial, cancellation);
            // With video xfade/concat normalized to the fixed frame rate, no
            // source region can be silently omitted or substituted.
            if (Math.Abs(decodedFrames - plan.ExpectedFrames) > 1)
                throw new InvalidDataException(
                    $"최종 프레임 수가 예상과 다릅니다: {plan.ExpectedFrames} 예상, {decodedFrames} 실제");
            cancellation.ThrowIfCancellationRequested();
            File.Move(outputPartial, path);
            progress?.Report(new VideoMergeProgress(clips.Count + 2, clips.Count + 2,
                "병합 완료 · 영상 프레임 검증 통과"));
            return new VideoMergeResult(path, plan.Width, plan.Height, plan.FramesPerSecond,
                decodedFrames, settings.Transition, clips.Count);
        }
        finally
        {
            try { if (File.Exists(outputPartial)) File.Delete(outputPartial); } catch (IOException) { }
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private async Task NormalizeAsync(VideoMergeClip clip, string target,
        VideoMergePlan plan, int frames, bool withAudio, CancellationToken cancellation)
    {
        var inv = CultureInfo.InvariantCulture;
        string F(double value) => value.ToString("0.#########", inv);
        var range = clip.Range;
        var start = clip.Index.BoundaryTime(range.Start) - clip.Index.StartTime;
        var end = clip.Index.BoundaryTime(range.End) - clip.Index.StartTime;
        var duration = frames / (double)plan.FramesPerSecond;

        var graph = new StringBuilder();
        graph.Append($"[0:v:0]select='gte(n,{range.Start})*lt(n,{range.End})'," +
            $"setpts=PTS-STARTPTS,fps={plan.FramesPerSecond}," +
            $"scale={plan.Width}:{plan.Height}:force_original_aspect_ratio=decrease:" +
            $"force_divisible_by=2,pad={plan.Width}:{plan.Height}:(ow-iw)/2:(oh-ih)/2," +
            "setsar=1,format=yuv420p,settb=AVTB[v]");
        if (withAudio)
        {
            if (clip.Source.HasAudio)
                graph.Append($";[0:a:0]atrim=start={F(start)}:end={F(end)}," +
                    "asetpts=PTS-STARTPTS,aresample=48000:async=1:first_pts=0," +
                    $"aformat=channel_layouts=stereo,apad,atrim=duration={F(duration)}," +
                    "asetpts=PTS-STARTPTS[a]");
            else
                graph.Append($";anullsrc=r=48000:cl=stereo,atrim=duration={F(duration)}," +
                    "asetpts=PTS-STARTPTS[a]");
        }

        var args = new List<string> {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-i", clip.Source.Path,
            "-filter_complex", graph.ToString(), "-map", "[v]"
        };
        if (withAudio) args.AddRange(new[] { "-map", "[a]", "-c:a", "aac", "-b:a", "160k" });
        else args.Add("-an");
        args.AddRange(new[] {
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "17",
            "-pix_fmt", "yuv420p", "-r", plan.FramesPerSecond.ToString(inv),
            "-frames:v", frames.ToString(inv),
            "-movflags", "+faststart", "-y", target
        });
        await FfProcess.CaptureAsync(FfmpegPath, args, cancellation);
        if (await Probe.CountDecodedFramesAsync(target, cancellation) != frames)
            throw new InvalidDataException("중간 병합 구간의 프레임 수가 예상과 다릅니다.");
    }

    private async Task ComposeAsync(IReadOnlyList<string> segments, VideoMergePlan plan,
        VideoMergeSettings settings, string path, string encoder, CancellationToken cancellation)
    {
        var inv = CultureInfo.InvariantCulture;
        string F(double d) => d.ToString("0.#########", inv);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        foreach (var file in segments) args.AddRange(new[] { "-i", file });

        var graph = new StringBuilder();
        var withAudio = settings.KeepAudio;
        string video;
        string? audio = null;
        if (settings.Transition == MergeTransitionMode.Cut)
        {
            for (var i = 0; i < segments.Count; i++)
                graph.Append(withAudio ? $"[{i}:v:0][{i}:a:0]" : $"[{i}:v:0]");
            graph.Append($"concat=n={segments.Count}:v=1:a={(withAudio ? 1 : 0)}[vout]");
            if (withAudio) graph.Append("[aout]");
            video = "[vout]";
            if (withAudio) audio = "[aout]";
        }
        else
        {
            var duration = F(plan.TransitionSeconds);
            var visual = settings.Transition == MergeTransitionMode.Crossfade
                ? "fade" : "fadeblack";
            video = "[0:v:0]";
            audio = withAudio ? "[0:a:0]" : null;
            var accumulatedFrames = plan.ClipFrames[0];
            for (var i = 1; i < segments.Count; i++)
            {
                var nextV = $"[v{i}]";
                var offsetFrames = accumulatedFrames - plan.TransitionFrames;
                graph.Append($"{video}[{i}:v:0]xfade=transition={visual}:" +
                    $"duration={duration}:offset={F(offsetFrames / (double)plan.FramesPerSecond)}" +
                    $"{nextV};");
                video = nextV;
                if (withAudio)
                {
                    var nextA = $"[a{i}]";
                    graph.Append($"{audio}[{i}:a:0]acrossfade=d={duration}:c1=tri:c2=tri{nextA};");
                    audio = nextA;
                }
                accumulatedFrames += plan.ClipFrames[i] - plan.TransitionFrames;
            }
            if (graph.Length > 0 && graph[^1] == ';') graph.Length--;
        }
        args.AddRange(new[] { "-filter_complex", graph.ToString(), "-map", video });
        if (withAudio && audio is not null)
            args.AddRange(new[] { "-map", audio, "-c:a", "aac", "-b:a", "192k" });
        else args.Add("-an");
        args.AddRange(new[] { "-c:v", encoder });
        if (encoder == "h264_nvenc")
            args.AddRange(new[] { "-preset", "p5", "-rc:v", "vbr", "-cq:v", "19", "-b:v", "0" });
        else
            args.AddRange(new[] { "-preset", "medium", "-crf", "19" });
        args.AddRange(new[] { "-r", plan.FramesPerSecond.ToString(inv), "-fps_mode:v", "cfr",
            "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-y", path });
        await FfProcess.CaptureAsync(FfmpegPath, args, cancellation);
    }
}
