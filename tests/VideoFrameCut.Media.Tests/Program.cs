using VideoFrameCut.Media;

if (args.Length < 2) throw new ArgumentException("Usage: <input-av1-opus> <cache-dir>");
var input = args[0];
var dir = args[1];
var probe = new MediaProbe();
var source = await probe.ProbeAsync(input);
if (source.Codec != "av1" || source.AudioCodec != "opus" ||
    !PreviewProxyService.NeedsProxy(source))
    throw new InvalidDataException($"Expected AV1+Opus source: {source}");

var h264 = source with { Codec = "h264", AudioCodec = "aac" };
if (PreviewProxyService.NeedsProxy(h264))
    throw new Exception("Native H.264/AAC should not be unnecessarily transcoded.");

var converter = new PreviewProxyService(cacheDirectory: dir);
var preview = await converter.GetOrCreateAsync(source);
var converted = await probe.ProbeAsync(preview);
if (converted.Codec != "h264" || converted.AudioCodec != "aac" ||
    converted.Width > 960 || converted.Height > 720)
    throw new InvalidDataException($"Invalid native-playback preview: {converted}");

var count = await probe.CountDecodedFramesAsync(preview);
if (count < 12) throw new Exception($"Preview lost too many frames: {count}");
var cached = await converter.GetOrCreateAsync(source);
if (preview != cached) throw new Exception("Preview cache key mismatch.");
if (await probe.ProbeAsync(input) is not { Codec: "av1", AudioCodec: "opus" })
    throw new Exception("Source was modified by proxy generation.");

Console.WriteLine($"PASS AV1+Opus -> H264+AAC preview: {converted.Width}x{converted.Height}, " +
                  $"{count} decodable frames; second use from cache, original unchanged");
