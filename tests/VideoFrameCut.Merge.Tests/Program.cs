using VideoFrameCut.Core;
using VideoFrameCut.Export;
using VideoFrameCut.Media;

if (args.Length != 4) throw new ArgumentException("3 input video files + output directory expected.");

var probe = new MediaProbe();
var sources = new List<VideoMergeClip>();
var spans = new[] { (3, 27), (9, 39), (4, 28) };
for (var i = 0; i < 3; i++)
{
    var source = await probe.ProbeAsync(args[i]);
    var index = await probe.BuildFrameIndexAsync(args[i], source.NominalFramesPerSecond);
    var (start, end) = spans[i];
    sources.Add(new VideoMergeClip(source, index, new FrameRange(start, end)));
}

var first = sources[0];
var fps = (int)Math.Round(first.Source.NominalFramesPerSecond!.Value);
if (fps != 24) throw new Exception("Expected first source at 24 FPS.");

var exporter = new VideoMergeExporter();
foreach (var (mode, slug, overlap) in new[]
{
    (MergeTransitionMode.Cut, "cut", 0),
    (MergeTransitionMode.Crossfade, "crossfade", 6),
    (MergeTransitionMode.FadeThroughBlack, "fadeblack", 6)
})
{
    var settings = new VideoMergeSettings(mode, 0.25, true, EncodingMode.Software);
    var plan = VideoMergeExporter.Plan(sources, settings);
    if (plan.TransitionFrames != overlap)
        throw new Exception("Unexpected transition frame count: " + plan.TransitionFrames);
    var output = Path.Combine(args[3], "merged_" + slug + ".mp4");
    var result = await exporter.ExportAsync(sources, output, settings);
    if (!File.Exists(output) || Math.Abs(result.OutputFrames - plan.ExpectedFrames) > 1)
        throw new Exception("Incorrect render frame count: " + slug);
    var info = await probe.ProbeAsync(output);
    if (info.Codec != "h264" || !info.HasAudio || info.AudioCodec != "aac" ||
        info.Width != first.Source.Width || info.Height != first.Source.Height)
        throw new Exception("Incompatible merged video/audio format: " + info);
    Console.WriteLine("PASS " + slug + " merge 3 heterogeneous sources; " +
        result.OutputFrames + " frames / " + info.Width + "x" + info.Height);
}

// A transition may not exceed the duration of any clip.
try
{
    _ = VideoMergeExporter.Plan(sources,
        new VideoMergeSettings(MergeTransitionMode.Crossfade, 2.0));
    throw new Exception("An overly long transition was accepted.");
}
catch (ArgumentException) { }

var one = new[] { sources[0] };
try
{
    _ = VideoMergeExporter.Plan(one, new VideoMergeSettings());
    throw new Exception("A one-clip merge was allowed.");
}
catch (ArgumentException) { }

Console.WriteLine("PASS transition validation for short clips and one-entry queue");
