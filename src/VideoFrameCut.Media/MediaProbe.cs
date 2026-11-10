using System.Globalization;
using System.Text.Json;
using VideoFrameCut.Core;

namespace VideoFrameCut.Media;

public sealed class MediaProbe
{
    public string FfprobePath { get; }

    public MediaProbe(string ffprobePath = "ffprobe") => FfprobePath = ffprobePath;

    public async Task<SourceVideoInfo> ProbeAsync(string path, CancellationToken cancellation = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Source video not found.", path);
        var data = await FfProcess.CaptureAsync(FfprobePath,
            new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", path }, cancellation);
        using var document = JsonDocument.Parse(data);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var videos = streams.Where(s => StringProperty(s, "codec_type") == "video").ToArray();
        if (videos.Length == 0) throw new InvalidDataException("No video stream present.");
        var video = videos[0];
        var fps = ParseRatio(StringProperty(video, "avg_frame_rate"));
        if (fps is null or <= 0) fps = ParseRatio(StringProperty(video, "r_frame_rate"));
        double? containerDuration = null;
        if (document.RootElement.TryGetProperty("format", out var format))
            containerDuration = ParseDouble(StringProperty(format, "duration"));

        return new SourceVideoInfo(
            path, video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(),
            StringProperty(video, "codec_name") ?? "unknown", fps,
            streams.Any(s => StringProperty(s, "codec_type") == "audio"), containerDuration);
    }

    public async Task<DisplayFrameIndex> BuildFrameIndexAsync(string path, double? nominalFps = null,
        IProgress<int>? progress = null, CancellationToken cancellation = default)
    {
        var timestamps = new List<double>(32_768);
        double? finalDuration = null;
        await FfProcess.ForEachLineAsync(FfprobePath,
            new[] { "-v", "error", "-select_streams", "v:0", "-show_frames",
                "-show_entries", "frame=best_effort_timestamp_time,pkt_duration_time",
                "-of", "compact=p=0:nk=0", path },
            line =>
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                string? ptsText = null;
                string? durationText = null;
                foreach (var field in line.Split('|'))
                {
                    if (field.StartsWith("best_effort_timestamp_time=", StringComparison.Ordinal))
                        ptsText = field["best_effort_timestamp_time=".Length..];
                    else if (field.StartsWith("pkt_duration_time=", StringComparison.Ordinal))
                        durationText = field["pkt_duration_time=".Length..];
                }
                // Some codecs include extra side_data entries in the printout; ignore those lines.
                if (ptsText is null)
                {
                    if (line.Contains("side_data_type=", StringComparison.Ordinal)) return;
                    throw new InvalidDataException($"Missing display timestamp: {line}");
                }
                var pts = ParseDouble(ptsText) ?? throw new InvalidDataException($"Invalid display timestamp: {ptsText}");
                timestamps.Add(pts);
                finalDuration = ParseDouble(durationText);
                if (timestamps.Count % 5_000 == 0) progress?.Report(timestamps.Count);
            }, cancellation);

        if (timestamps.Count == 0) throw new InvalidDataException("No decodable display frames.");
        var fallbackFrameDuration = timestamps.Count > 1
            ? timestamps[^1] - timestamps[^2]
            : nominalFps is > 0 ? 1.0 / nominalFps.Value : 1.0 / 30.0;
        if (!double.IsFinite(fallbackFrameDuration) || fallbackFrameDuration <= 0)
            fallbackFrameDuration = 1.0 / 30.0;
        var lastDuration = finalDuration is > 0 ? finalDuration.Value : fallbackFrameDuration;
        progress?.Report(timestamps.Count);
        return new DisplayFrameIndex(timestamps, timestamps[^1] + lastDuration);
    }

    public async Task<int> CountDecodedFramesAsync(string path, CancellationToken cancellation = default)
    {
        var result = await FfProcess.CaptureAsync(FfprobePath,
            new[] { "-v", "error", "-count_frames", "-select_streams", "v:0",
                "-show_entries", "stream=nb_read_frames",
                "-of", "default=noprint_wrappers=1:nokey=1", path }, cancellation);
        if (!int.TryParse(result.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0)
            throw new InvalidDataException($"Invalid decoded frame count: {result.Trim()}");
        return count;
    }

    private static string? StringProperty(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();
    }

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
            ? parsed : null;

    private static double? ParseRatio(string? value)
    {
        if (value is null) return null;
        var parts = value.Split('/');
        if (parts.Length == 2 && ParseDouble(parts[0]) is { } numerator && ParseDouble(parts[1]) is > 0 and var denominator)
            return numerator / denominator;
        return ParseDouble(value);
    }
}
