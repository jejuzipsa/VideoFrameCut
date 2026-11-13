using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using VideoFrameCut.Media;

var root = Path.Combine(Path.GetTempPath(), "FrameCut-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var zip = Path.Combine(root, "mock-essentials.zip");
    var exeBytes = new byte[12_000];
    exeBytes[0] = (byte)'M';
    exeBytes[1] = (byte)'Z';
    for (int i = 2; i < exeBytes.Length; i++) exeBytes[i] = (byte)(i % 251);

    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
    {
        foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
        {
            var entry = archive.CreateEntry("ffmpeg-test-essentials_build/bin/" + name);
            using var stream = entry.Open();
            stream.Write(exeBytes);
        }
    }

    var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
    var checksum = FFmpegBootstrapper.ParseChecksum(expected + "  ffmpeg-release-essentials.zip");
    Require(checksum == expected, "Failed to read provider checksum");
    FFmpegBootstrapper.VerifyChecksum(zip, checksum);
    using (var archive = ZipFile.OpenRead(zip))
    {
        var (ffmpeg, ffprobe) = FFmpegBootstrapper.FindTools(archive);
        Require(ffmpeg.Name == "ffmpeg.exe" && ffprobe.Name == "ffprobe.exe",
            "Selected wrong tools from publisher ZIP");
    }

    try
    {
        FFmpegBootstrapper.ParseChecksum("malformed");
        throw new Exception("Malformed checksum was accepted");
    }
    catch (InvalidDataException) { }

    try
    {
        FFmpegBootstrapper.VerifyChecksum(zip, new string('0', 64));
        throw new Exception("Invalid file digest was accepted");
    }
    catch (InvalidDataException) { }

    // Exercise the whole downloader with an in-memory HTTPS fixture, not
    // an uncontrolled 100 MB network request on every CI run.
    if (OperatingSystem.IsWindows())
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", root);
            var zipBytes = File.ReadAllBytes(zip);
            using var http = new HttpClient(new MockHandler(zipBytes,
                Encoding.ASCII.GetBytes(expected)));
            var destination = Path.Combine(root, "installed");
            var installer = new FFmpegBootstrapper();
            Require(await installer.EnsureAsync(destination, client: http),
                "Expected a first-run installation.");
            Require(File.ReadAllBytes(Path.Combine(destination, "ffmpeg.exe"))
                    .SequenceEqual(exeBytes), "Installed ffmpeg binary differs from ZIP");
            Require(File.ReadAllBytes(Path.Combine(destination, "ffprobe.exe"))
                    .SequenceEqual(exeBytes), "Installed ffprobe binary differs from ZIP");
            Require(!await installer.EnsureAsync(destination, client: http),
                "Existing tools should not be downloaded again.");

            var rejected = Path.Combine(root, "invalid-checksum");
            using var wrongHttp = new HttpClient(new MockHandler(zipBytes,
                Encoding.ASCII.GetBytes(new string('0', 64))));
            try
            {
                await installer.EnsureAsync(rejected, client: wrongHttp);
                throw new Exception("Unverified archive installed successfully");
            }
            catch (InvalidDataException) { }
            Require(!File.Exists(Path.Combine(rejected, "ffmpeg.exe")) &&
                    !File.Exists(Path.Combine(rejected, "ffprobe.exe")),
                "Hash verification failure left an executable behind.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    Console.WriteLine("PASS FFmpeg bootstrap: hash verification, ZIP validation, portable extraction, cache skip and rejected tampering");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class MockHandler(byte[] zip, byte[] digest) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.AbsoluteUri;
        byte[] bytes = url switch
        {
            FFmpegBootstrapper.ArchiveUrl => zip,
            FFmpegBootstrapper.ChecksumUrl => digest,
            _ => throw new InvalidOperationException("Unexpected download URL: " + url)
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        });
    }
}
