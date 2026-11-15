using System.Diagnostics;
using System.Security.Cryptography;
using VideoFrameCut.Export;
using VideoFrameCut.Media;

// Runs on Ubuntu CI with FFmpeg / FFprobe installed.
// Exercises actual encoders and image geometry, not just argument strings.
var root = Path.Combine(Path.GetTempPath(), "framecut-transcode-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var sourcePath = Path.Combine(root, "quadrants.mp4");
    var sourceArgs = new List<string> { "-nostdin", "-v", "error" };
    foreach (var color in new[] { "red", "green", "blue", "yellow" })
        sourceArgs.AddRange(new[] { "-f", "lavfi", "-i",
            $"color=c={color}:s=80x46:r=60:d=1" });
    sourceArgs.AddRange(new[] {
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1",
        "-filter_complex",
        "[0:v][1:v]hstack=inputs=2[top];" +
        "[2:v][3:v]hstack=inputs=2[bottom];" +
        "[top][bottom]vstack=inputs=2[v]",
        "-map", "[v]", "-map", "4:a:0",
        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "192k", "-shortest", "-y", sourcePath
    });
    await FfProcess.CaptureAsync("ffmpeg", sourceArgs);

    var probe = new MediaProbe();
    var info = await probe.ProbeAsync(sourcePath);
    Check(info.Width == 160 && info.Height == 92 && info.HasAudio &&
          info.NominalFramesPerSecond is > 59, "60fps fixture invalid");
    var hashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)));

    var exporter = new VideoTranscodeExporter();
    var landscape4k = new SourceVideoInfo(sourcePath, 3840, 2160, "hevc",
        59.94, true, 181, "aac", "arib-std-b67", "bt2020", "bt2020nc", "yuv420p10le");
    var portrait4k = landscape4k with { Width = 2160, Height = 3840 };
    var qualityPlan = VideoTranscodeExporter.CreatePlan(landscape4k,
        new VideoTransformOptions(CompressionPreset.HighQuality));
    var balancedPlan = VideoTranscodeExporter.CreatePlan(landscape4k,
        new VideoTransformOptions(CompressionPreset.Balanced));
    var smallPlan = VideoTranscodeExporter.CreatePlan(landscape4k,
        new VideoTransformOptions(CompressionPreset.SmallFile));
    Check((qualityPlan.Width, qualityPlan.Height) == (3840, 2160) &&
          (balancedPlan.Width, balancedPlan.Height) == (1920, 1080) &&
          (smallPlan.Width, smallPlan.Height) == (1280, 720),
        "Landscape presets must be original, 1080p, 720p");
    Check(smallPlan.FramesPerSecond == 30 && smallPlan.IsHdr && smallPlan.TenBit &&
          qualityPlan.CopyAudio && !smallPlan.CopyAudio,
        "HLG metadata and audio compression flags lost");
    Check((VideoTranscodeExporter.CreatePlan(portrait4k,
        new VideoTransformOptions(CompressionPreset.Balanced, 90)).Width,
           VideoTranscodeExporter.CreatePlan(portrait4k,
        new VideoTransformOptions(CompressionPreset.Balanced, 90)).Height) == (1920, 1080),
        "Portrait rotation must swap 1080x1920 to 1920x1080");

    var qualityFile = Path.Combine(root, "high.mp4");
    var high = await exporter.ExportAsync(info, qualityFile,
        new VideoTransformOptions(CompressionPreset.HighQuality));
    Check((await probe.ProbeAsync(qualityFile)).CodecTag == "hvc1",
        "High-quality output must use hvc1 for MP4 thumbnail compatibility.");
    Check(high.Width == 160 && high.Height == 92 && high.FileSizeBytes > 0,
        "High quality should not upscale or destroy pixels");
    Check((await probe.ProbeAsync(qualityFile)).AudioCodec == "aac",
        "Audio should remain AAC");

    var rotatedFile = Path.Combine(root, "rotated-and-mirrored.mp4");
    var rotated = await exporter.ExportAsync(info, rotatedFile,
        new VideoTransformOptions(CompressionPreset.Balanced, 90, true));
    Check((rotated.Width, rotated.Height) == (92, 160),
        "90-degree rotation must exchange frame width and height");
    Check(string.Join(',', await Corners(rotatedFile, 92, 160)) == "red,blue,green,yellow",
        "90deg rotation followed by horizontal mirror placed image quadrants incorrectly");
    Console.WriteLine("PASS 90 degree rotation and horizontal flip, verified actual image pixels");

    var smallFile = Path.Combine(root, "small.mp4");
    var small = await exporter.ExportAsync(info, smallFile,
        new VideoTransformOptions(CompressionPreset.SmallFile, 0, true));
    var smallInfo = await probe.ProbeAsync(smallFile);
    Check(smallInfo.CodecTag == "hvc1",
        "Small-file output must not use FFmpeg's incompatible default hev1 tag.");
    Check(smallInfo.NominalFramesPerSecond is > 29 and < 31 &&
        smallInfo.AudioCodec == "aac", "Small preset must cap 60fps at 30fps and encode AAC");
    Check(string.Join(',', await Corners(smallFile, 160, 92)) == "green,red,yellow,blue",
        "Standalone horizontal mirror placed image quadrants incorrectly");
    Console.WriteLine("PASS three compression presets with original audio and AAC recompression");

    // Camera MOVs often store landscape pixels (e.g. 3840x2160) but a
    // 90-degree display matrix, so they must be planned as portrait BEFORE
    // applying the user's chosen 180-degree/90-degree output rotation.
    var metadataRotated = Path.Combine(root, "phone-rotated.mov");
    await FfProcess.CaptureAsync("ffmpeg", new[] {
        "-nostdin", "-v", "error",
        "-display_rotation", "90",
        "-i", sourcePath,
        "-map", "0", "-c", "copy",
        "-y", metadataRotated
    });
    var phone = await probe.ProbeAsync(metadataRotated);
    Check(phone.Width == 160 && phone.Height == 92 &&
        phone.DisplayRotationDegrees is 90 or 270,
        "Phone MOV display matrix should be reported by ffprobe as a 90-degree rotation.");

    var phonePlan = VideoTranscodeExporter.CreatePlan(phone,
        new VideoTransformOptions(CompressionPreset.HighQuality, 180));
    Check((phonePlan.Width, phonePlan.Height) == (92, 160),
        "Display-rotated phone 160x92 + 180° must produce 92x160, not 160x92.");

    // Simulate the exact 3840x2160 camera file from the screenshot.
    var landscapeWithDisplayRotation = landscape4k with
    {
        DisplayRotationDegrees = phone.DisplayRotationDegrees
    };
    var rotated4kPlan = VideoTranscodeExporter.CreatePlan(
        landscapeWithDisplayRotation,
        new VideoTransformOptions(CompressionPreset.HighQuality, 180));
    Check((rotated4kPlan.Width, rotated4kPlan.Height) == (2160, 3840),
        "4K landscape-coded MOV with a 90° display matrix should output portrait 2160x3840.");
    var rotated1080pPlan = VideoTranscodeExporter.CreatePlan(
        landscapeWithDisplayRotation,
        new VideoTransformOptions(CompressionPreset.Balanced, 180));
    Check((rotated1080pPlan.Width, rotated1080pPlan.Height) == (1080, 1920),
        "Balanced preset must scale the displayed portrait, not the raw encoded landscape.");
    var rotatedRight90Plan = VideoTranscodeExporter.CreatePlan(
        landscapeWithDisplayRotation,
        new VideoTransformOptions(CompressionPreset.HighQuality, 90));
    Check((rotatedRight90Plan.Width, rotatedRight90Plan.Height) == (3840, 2160),
        "A further 90° user rotation should switch portrait display back to landscape.");

    var metadataUnrotated = Path.Combine(root, "phone-orientation-normal.mp4");
    var normalPhone = await exporter.ExportAsync(phone, metadataUnrotated,
        new VideoTransformOptions(CompressionPreset.HighQuality));
    Check((normalPhone.Width, normalPhone.Height) == (92, 160),
        "MOV metadata autorotation must be included in output dimensions.");
    var referenceCorners = await Corners(metadataUnrotated, 92, 160);
    var correctedPhone = Path.Combine(root, "phone-orientation-180.mp4");
    var corrected = await exporter.ExportAsync(phone, correctedPhone,
        new VideoTransformOptions(CompressionPreset.HighQuality, 180));
    var correctedInfo = await probe.ProbeAsync(correctedPhone);
    Check((correctedInfo.Width, correctedInfo.Height) == (92, 160) &&
        correctedInfo.DisplayRotationDegrees == 0 &&
        correctedInfo.CodecTag == "hvc1",
        "Metadata-rotated MOV output must be portrait, hvc1, with no stale rotation tags.");
    var correctedCorners = await Corners(correctedPhone, 92, 160);
    Check(correctedCorners.SequenceEqual(referenceCorners.Reverse()),
        "The user-selected 180° must rotate the already-display-oriented MOV frames.");
    Console.WriteLine("PASS MOV display-matrix autorotation, 4K planning and 180° user rotation");

    var invertedFile = Path.Combine(root, "rotated-180.mp4");
    await exporter.ExportAsync(info, invertedFile,
        new VideoTransformOptions(CompressionPreset.HighQuality, 180));
    Check(string.Join(',', await Corners(invertedFile, 160, 92)) == "yellow,blue,green,red",
        "180-degree rotation placed image quadrants incorrectly");
    Console.WriteLine("PASS 180 degree rotation verified actual image pixels");

    var hlgFile = Path.Combine(root, "hlg-source.mp4");
    await FfProcess.CaptureAsync("ffmpeg", new[] {
        "-nostdin", "-v", "error", "-f", "lavfi",
        "-i", "testsrc2=size=160x90:rate=24:duration=0.5",
        "-vf", "format=yuv420p10le",
        "-c:v", "libx265", "-preset", "ultrafast",
        "-x265-params", "pools=1:frame-threads=1:log-level=error:colorprim=9:transfer=18:colormatrix=9",
        "-color_primaries", "bt2020", "-color_trc", "arib-std-b67",
        "-colorspace", "bt2020nc", "-y", hlgFile
    });
    var hlg = await probe.ProbeAsync(hlgFile);
    Check(hlg.ColorTransfer == "arib-std-b67" &&
        hlg.ColorPrimaries == "bt2020", "Input HLG fixture lacks proper HDR tags");
    var hlgResult = await exporter.ExportAsync(hlg,
        Path.Combine(root, "hlg-output.mp4"),
        new VideoTransformOptions(CompressionPreset.SmallFile));
    var hlgOutput = await probe.ProbeAsync(hlgResult.OutputPath);
    Check(hlgOutput.CodecTag == "hvc1", "HDR HLG MP4 should use hvc1.");
    Check(hlgResult.HdrPreserved && hlgOutput.ColorTransfer == "arib-std-b67" &&
          hlgOutput.ColorPrimaries == "bt2020" &&
          hlgOutput.PixelFormat?.Contains("10", StringComparison.Ordinal) == true,
        "Transcoding HLG must retain 10-bit and BT.2020 transfer/primaries");
    Console.WriteLine("PASS HLG HDR metadata and 10-bit preservation");

    Check(hashBefore == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))),
        "Original video file was modified");
    try
    {
        await exporter.ExportAsync(info, sourcePath, new VideoTransformOptions());
        throw new Exception("Overwriting source was allowed");
    }
    catch (ArgumentException) { }
    Check(hashBefore == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))),
        "Source changed after rejected overwrite");
    Console.WriteLine("PASS original files unchanged and overwrite prevented");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task<IReadOnlyList<string>> Corners(string file, int width, int height)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        }
    };
    foreach (var arg in new[] { "-nostdin", "-v", "error", "-i", file,
        "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "-" })
        process.StartInfo.ArgumentList.Add(arg);
    process.Start();
    await using var buffer = new MemoryStream();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.StandardOutput.BaseStream.CopyToAsync(buffer);
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
        throw new Exception("Failed to decode transformed video: " + await stderr);
    var rgb = buffer.ToArray();
    Check(rgb.Length == width * height * 3, "RGB frame dimensions changed unexpectedly");
    string Color(int x, int y)
    {
        var idx = (y * width + x) * 3;
        var pixel = (R: (int)rgb[idx], G: (int)rgb[idx + 1], B: (int)rgb[idx + 2]);
        var palette = new[]
        {
            (Name: "red", R: 255, G: 0, B: 0),
            (Name: "green", R: 0, G: 128, B: 0),
            (Name: "blue", R: 0, G: 0, B: 255),
            (Name: "yellow", R: 255, G: 255, B: 0)
        };
        return palette.MinBy(p =>
            (p.R - pixel.R) * (p.R - pixel.R) +
            (p.G - pixel.G) * (p.G - pixel.G) +
            (p.B - pixel.B) * (p.B - pixel.B)).Name;
    }
    return new[] { Color(width / 4, height / 4),
        Color(width * 3 / 4, height / 4),
        Color(width / 4, height * 3 / 4),
        Color(width * 3 / 4, height * 3 / 4) };
}
