using System.Globalization;
using VideoFrameCut.Core;
using VideoFrameCut.Export;
using VideoFrameCut.Media;

try
{
    if (args.Length < 2 || args[0] is not ("info" or "split" or "extract"))
    {
        Help();
        return 2;
    }
    var mode = args[0];
    var source = args[1];
    var options = Options(args.Skip(2).ToArray());
    var probe = new MediaProbe();
    var media = await probe.ProbeAsync(source);
    Console.WriteLine($"{media.Width}x{media.Height}, {media.Codec}, FPS: {media.NominalFramesPerSecond}, audio: {media.HasAudio}");
    var countProgress = new Progress<int>(n => Console.Write($"\rIndexed {n:N0} frames..."));
    var index = await probe.BuildFrameIndexAsync(source, media.NominalFramesPerSecond, countProgress);
    Console.WriteLine($"\nDisplay frames: {index.FrameCount}, time: {index.StartTime:F3} - {index.EndTime:F3}");
    if (mode == "info") return 0;

    if (!options.TryGetValue("out", out var outFolder) || string.IsNullOrWhiteSpace(outFolder))
        throw new ArgumentException("Missing --out output_folder.");

    IReadOnlyList<EditSegment> selected;
    if (mode == "split")
    {
        var seconds = double.Parse(Get(options, "seconds"), CultureInfo.InvariantCulture);
        var editor = new ClipEditor(index);
        editor.SplitByTime(seconds);
        selected = editor.Segments;
        if (options.TryGetValue("segments", out var list))
        {
            var numbers = ParseSelections(list);
            selected = selected.Where((_, i) => numbers.Contains(i + 1)).ToArray();
        }
    }
    else
    {
        var start = int.Parse(Get(options, "start-frame"), CultureInfo.InvariantCulture);
        var end = int.Parse(Get(options, "end-frame"), CultureInfo.InvariantCulture);
        var range = new FrameRange(start, end);
        if (range.End > index.FrameCount) throw new ArgumentOutOfRangeException(nameof(end));
        selected = new[] { new EditSegment(Guid.NewGuid(), range) };
    }
    if (selected.Count == 0) throw new ArgumentException("No segments selected.");

    Console.WriteLine($"Exporting {selected.Count} clips...");
    var settings = new ExportSettings(
        options.ContainsKey("cpu") ? EncodingMode.Software : EncodingMode.Auto,
        !options.ContainsKey("no-audio"));
    var exporter = new ClipExporter();
    var progress = new Progress<ExportProgress>(p =>
        Console.WriteLine($"{p.Completed}/{p.Total} {p.CurrentOutput} - {p.Status}"));
    var report = await exporter.ExportAsync(media, index, selected, outFolder, settings, progress);
    foreach (var clip in report.Clips)
        Console.WriteLine($"{(clip.Succeeded ? "PASS" : "FAIL")} {clip.OutputFile}: " +
            $"{clip.DecodedFrames}/{clip.ExpectedFrames} frames {clip.Error}");
    Console.WriteLine($"Manifest: {Path.Combine(outFolder, "framecut_manifest.json")}");
    return report.Clips.Count == selected.Count && report.Clips.All(c => c.Succeeded) ? 0 : 1;
}
catch (Exception error)
{
    Console.Error.WriteLine($"ERROR: {error}");
    return 1;
}

static string Get(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"Missing --{name}.");

static Dictionary<string, string> Options(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < values.Length; i++)
    {
        var token = values[i];
        if (!token.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected option: {token}");
        var name = token[2..];
        if (name is "cpu" or "no-audio") result.Add(name, "true");
        else if (i + 1 < values.Length) result.Add(name, values[++i]);
        else throw new ArgumentException($"Missing value for {token}");
    }
    return result;
}

static HashSet<int> ParseSelections(string value)
{
    var result = new HashSet<int>();
    foreach (var item in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    {
        var range = item.Split('-');
        if (range.Length == 1) result.Add(int.Parse(item, CultureInfo.InvariantCulture));
        else if (range.Length == 2)
        {
            var start = int.Parse(range[0], CultureInfo.InvariantCulture);
            var end = int.Parse(range[1], CultureInfo.InvariantCulture);
            if (start < 1 || end < start) throw new ArgumentException("Invalid segment selection range.");
            for (var i = start; i <= end; i++) result.Add(i);
        }
        else throw new ArgumentException($"Invalid segment selection: {item}");
    }
    return result;
}

static void Help()
{
    Console.WriteLine("VideoFrameCut CLI (initial engine test)");
    Console.WriteLine("  framecut info <source.mp4>");
    Console.WriteLine("  framecut split <source.mp4> --seconds 25 --out <folder> [--segments 2-7] [--cpu] [--no-audio]");
    Console.WriteLine("  framecut extract <source.mp4> --start-frame 300 --end-frame 600 --out <folder> [--cpu]");
}
