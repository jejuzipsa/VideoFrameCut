using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VideoFrameCut.Media;

/// <summary>
/// Creates a small, Windows MediaElement-compatible H.264/AAC preview copy.
/// The source stays untouched and is always used for all frame-accurate edits
/// and exports. Proxy filenames are keyed to source path/size/mtime.
/// </summary>
public sealed class PreviewProxyService
{
    private static readonly HashSet<string> NonNativeVideo = new(StringComparer.OrdinalIgnoreCase)
    {
        "av1", "vp8", "vp9", "hevc", "h265", "prores", "theora", "ffv1"
    };

    private static readonly HashSet<string> NonNativeAudio = new(StringComparer.OrdinalIgnoreCase)
    {
        "opus", "vorbis", "flac", "alac"
    };

    public string FfmpegPath { get; }
    public MediaProbe Probe { get; }
    public string CacheDirectory { get; }

    public PreviewProxyService(string ffmpegPath = "ffmpeg", string ffprobePath = "ffprobe",
        string? cacheDirectory = null)
    {
        FfmpegPath = ffmpegPath;
        Probe = new MediaProbe(ffprobePath);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        CacheDirectory = cacheDirectory ?? Path.Combine(
            string.IsNullOrWhiteSpace(local) ? Path.GetTempPath() : local,
            "FrameCut", "PreviewCache");
    }

    public static bool NeedsProxy(SourceVideoInfo video) =>
        NonNativeVideo.Contains(video.Codec) ||
        (video.HasAudio && video.AudioCodec is { } audio && NonNativeAudio.Contains(audio));

    public async Task<string> GetOrCreateAsync(SourceVideoInfo source,
        IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!File.Exists(source.Path))
            throw new FileNotFoundException("Source video not found.", source.Path);

        Directory.CreateDirectory(CacheDirectory);
        var input = new FileInfo(source.Path);
        var keyData = string.Join("|", Path.GetFullPath(source.Path),
            input.Length.ToString(CultureInfo.InvariantCulture),
            input.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            "h264-aac-720p-preview-v1");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyData)))[..24];
        var result = Path.Combine(CacheDirectory, hash + ".mp4");

        if (File.Exists(result))
        {
            // A stale/partial zero-byte proxy must never be treated as usable.
            if (new FileInfo(result).Length > 1024)
            {
                progress?.Report(1.0);
                return result;
            }
            File.Delete(result);
        }

        var temporary = Path.Combine(CacheDirectory, hash + "_" +
            Guid.NewGuid().ToString("N") + ".partial.mp4");
        var hasAudio = source.HasAudio;
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-i", source.Path, "-map", "0:v:0"
        };
        if (hasAudio) args.AddRange(new[] { "-map", "0:a:0?" });
        else args.Add("-an");

        args.AddRange(new[]
        {
            "-vf",
            "setpts=PTS-STARTPTS,scale=w='min(960,iw)':h='min(720,ih)':" +
            "force_original_aspect_ratio=decrease:force_divisible_by=2:flags=fast_bilinear",
            "-fps_mode:v", "passthrough",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "27",
            "-pix_fmt", "yuv420p", "-bf:v", "0"
        });
        if (hasAudio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "128k" });
        args.AddRange(new[]
        {
            "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", temporary
        });

        try
        {
            await FfProcess.ForEachLineAsync(FfmpegPath, args, line =>
            {
                // FFmpeg reports microseconds under out_time_us / out_time_ms.
                // The alternate out_time field is a HH:mm:ss.ffffff string.
                if (source.ContainerDurationSeconds is not > 0) return;
                double? elapsed = null;
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
                    line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                {
                    var value = line[(line.IndexOf('=') + 1)..];
                    if (long.TryParse(value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var microseconds))
                        elapsed = microseconds / 1_000_000.0;
                }
                else if (line.StartsWith("out_time=", StringComparison.Ordinal) &&
                    TimeSpan.TryParse(line["out_time=".Length..], CultureInfo.InvariantCulture,
                        out var time))
                {
                    elapsed = time.TotalSeconds;
                }

                if (elapsed is >= 0)
                    progress?.Report(Math.Clamp(
                        elapsed.Value / source.ContainerDurationSeconds.Value, 0, 0.99));
            }, cancellation);

            cancellation.ThrowIfCancellationRequested();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length < 1024)
                throw new InvalidDataException("FFmpeg produced an empty playback proxy.");

            var validated = await Probe.ProbeAsync(temporary, cancellation);
            if (!string.Equals(validated.Codec, "h264", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Preview conversion did not produce H.264.");
            if (hasAudio && validated.AudioCodec is not "aac")
                throw new InvalidDataException("Preview conversion did not produce AAC audio.");

            File.Move(temporary, result, overwrite: true);
            progress?.Report(1.0);
            return result;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* Best effort cleanup on cancellation. */ }
        }
    }
}
